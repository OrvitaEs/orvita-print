<?php
/**
 * Autenticación REST del agente: Bearer con respaldo X-Orp-Token, búsqueda del
 * token por SHA-256 y límite de 120 peticiones/minuto por token en transients.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Rest;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Auth {

	/** Límite de peticiones por ventana (Doc/03 §8, S-04). */
	public const RATE_LIMIT_PER_WINDOW = 120;

	/** Ventana en segundos. */
	public const WINDOW_SECONDS = 60;

	/** Agente autenticado en la petición en curso (ciclo de vida de PHP: una petición). */
	private static ?array $current_agent = null;

	/**
	 * Callback de permisos REST: autentica el token y aplica el límite de
	 * peticiones. Si todo va bien, deja el agente autenticado en la petición.
	 *
	 * @return true|\WP_Error
	 */
	public static function check( \WP_REST_Request $request ) {
		$token = self::extract_token( $request );

		if ( null === $token || '' === $token ) {
			return self::error( 'orp_invalid_token', __( 'Falta el token del agente.', 'orp-print' ), 401 );
		}

		$agent = self::find_by_hash( hash( 'sha256', $token ) );

		if ( null === $agent ) {
			return self::error( 'orp_invalid_token', __( 'Token desconocido.', 'orp-print' ), 401 );
		}

		if ( 'active' !== $agent->status ) {
			return self::error( 'orp_token_revoked', __( 'Token revocado. Detén el agente y solicita uno nuevo.', 'orp-print' ), 401 );
		}

		$rate_limited = self::rate_limit( (int) $agent->id );
		if ( is_wp_error( $rate_limited ) ) {
			return $rate_limited;
		}

		// El controlador leerá de aquí el agente y su local.
		self::$current_agent = array(
			'id'           => (int) $agent->id,
			'location_id'  => (int) $agent->location_id,
			'name'         => (string) $agent->name,
			'token_prefix' => (string) $agent->token_prefix,
		);

		return true;
	}

	/**
	 * Genera un token nuevo. El token en claro solo se muestra una vez (al
	 * emparejar o al sembrar); en base de datos se guarda el SHA-256 y el
	 * prefijo de 8 caracteres para localizarlo (Doc/06 §11).
	 *
	 * @return array{token: string, hash: string, prefix: string}
	 */
	public static function generate_token(): array {
		$raw = 'orp_' . bin2hex( random_bytes( 24 ) );

		return array(
			'token'  => $raw,
			'hash'   => hash( 'sha256', $raw ),
			'prefix' => substr( $raw, 0, 8 ),
		);
	}

	/** Busca un agente por el hash SHA-256 de su token. */
	public static function find_by_hash( string $hash ): ?object {
		global $wpdb;

		$table = $wpdb->prefix . 'orp_agents';

		// phpcs:ignore WordPress.DB.DirectDatabaseQuery -- Tabla propia, sin caché: la revocación debe ser inmediata.
		$row = $wpdb->get_row(
			$wpdb->prepare(
				"SELECT id, location_id, name, status, token_prefix
				   FROM {$table}
				  WHERE token_hash = %s",
				$hash
			)
		);

		if ( null === $row ) {
			return null;
		}

		return $row;
	}

	/** Lee el agente autenticado que Auth::check dejó en la petición en curso. */
	public static function agent( \WP_REST_Request $request ): array {
		if ( is_array( self::$current_agent ) ) {
			return self::$current_agent;
		}

		return array(
			'id'           => 0,
			'location_id'  => 0,
			'name'         => '',
			'token_prefix' => '',
		);
	}

	/**
	 * Extrae el token: Authorization: Bearer primero, X-Orp-Token como
	 * respaldo (algunos hostings eliminan la cabecera Authorization).
	 */
	private static function extract_token( \WP_REST_Request $request ): ?string {
		$authorization = (string) $request->get_header( 'authorization' );

		if ( '' !== $authorization && preg_match( '/^Bearer\s+(\S+)$/i', $authorization, $matches ) ) {
			return $matches[1];
		}

		$fallback = $request->get_header( 'x_orp_token' );
		if ( null !== $fallback && '' !== trim( (string) $fallback ) ) {
			return trim( (string) $fallback );
		}

		return null;
	}

	/**
	 * Límite de peticiones: contador por token en un transient de 60 s.
	 * Devuelve true o un WP_Error 429 con cabecera Retry-After.
	 *
	 * @return true|\WP_Error
	 */
	private static function rate_limit( int $agent_id ) {
		$key   = 'orp_rl_' . $agent_id;
		$count = get_transient( $key );

		if ( false === $count ) {
			set_transient( $key, 1, self::WINDOW_SECONDS );
			return true;
		}

		$remaining = self::transient_ttl( $key );

		if ( (int) $count >= self::RATE_LIMIT_PER_WINDOW ) {
			header( 'Retry-After: ' . max( 1, $remaining ) );
			return self::error( 'orp_rate_limited', __( 'Demasiadas peticiones. Aplica retroceso.', 'orp-print' ), 429 );
		}

		// Conserva el vencimiento de la ventana en curso (no la reinicia).
		set_transient( $key, (int) $count + 1, max( 1, $remaining ) );

		return true;
	}

	/** Segundos que le quedan a la ventana actual del transient. */
	private static function transient_ttl( string $key ): int {
		$timeout = get_option( '_transient_timeout_' . $key );
		if ( $timeout ) {
			return max( 1, (int) $timeout - time() );
		}
		return self::WINDOW_SECONDS;
	}

	/**
	 * Error REST con el código del contrato (Doc/04 §6) y su estado HTTP.
	 *
	 * @param array<string, mixed> $extra Datos adicionales para el cuerpo.
	 */
	private static function error( string $code, string $message, int $status, array $extra = array() ): \WP_Error {
		return new \WP_Error( $code, $message, array_merge( array( 'status' => $status ), $extra ) );
	}
}
