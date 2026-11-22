<?php
/**
 * RuleMatcher — evaluación de las reglas JSON de orp_locations (Doc/04 §2.1).
 *
 * Semántica:
 * - Cada local tiene un array de reglas; un local coincide cuando **todas** sus
 *   reglas se cumplen (AND). El primer local que coincide por prioridad
 *   ascendente gana (la ordenación la hace LocationResolver).
 * - Tipos: city, postcode, shipping_method, product_category, order_meta.
 * - Operadores: in, equals, contains, prefix, exists.
 * - El matching de texto libre (city, product_category) se hace sobre texto
 *   normalizado: minúsculas y sin acentos («Alcalá» ≡ «alcala»).
 *
 * La lógica pura vive en matches_facts() (testeable sin WooCommerce); la
 * extracción de datos del pedido está en order_values()/order_matches().
 */

declare(strict_types=1);

namespace Orvita\PrintBridge\Domain;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class RuleMatcher {

	/** Tipos de regla admitidos (Doc/04 §2.1). */
	public const TYPES = array( 'city', 'postcode', 'shipping_method', 'product_category', 'order_meta' );

	/** Operadores admitidos (Doc/04 §2.1). */
	public const OPERATORS = array( 'in', 'equals', 'contains', 'prefix', 'exists' );

	/** Límite defensivo de reglas por local y de valores por regla. */
	public const MAX_RULES  = 20;
	public const MAX_VALUES = 50;

	/**
	 * Valores del pedido candidatos para un tipo de regla.
	 *
	 * Doc/05 §5: el checkout del piloto desactiva billing_city, así que se lee
	 * **shipping primero**: billing solo se usa si shipping viene vacío (nunca
	 * ambos), de modo que un pedido con shipping definido no puede enrutar por
	 * el billing.
	 *
	 * @param \WC_Order $order Pedido (API CRUD; nunca get_post_meta).
	 * @param string    $type  Tipo de regla.
	 * @return array<int, string> Valores candidatos ya normalizados.
	 */
	public static function order_values( \WC_Order $order, string $type ): array {
		switch ( $type ) {
			case 'city':
				$shipping = trim( (string) $order->get_shipping_city() );
				$raw      = array( '' !== $shipping ? $shipping : trim( (string) $order->get_billing_city() ) );
				break;

			case 'postcode':
				$shipping = trim( (string) $order->get_shipping_postcode() );
				$raw      = array( '' !== $shipping ? $shipping : trim( (string) $order->get_billing_postcode() ) );
				break;

			case 'shipping_method':
				$raw = array();
				foreach ( $order->get_items( 'shipping' ) as $item ) {
					$method_id   = (string) $item->get_method_id();
					$instance_id = (string) $item->get_instance_id();
					$raw[]       = '' !== $instance_id ? $method_id . ':' . $instance_id : $method_id;
					$raw[]       = $method_id; // El método sin instancia también es candidato.
				}
				break;

			case 'product_category':
				$raw = array();
				foreach ( $order->get_items() as $item ) {
					$product_id = (int) $item->get_product_id();
					// Variaciones: la categoría vive en el producto padre.
					$variation_id = (int) $item->get_variation_id();
					if ( $variation_id > 0 ) {
						$variation = \wc_get_product( $variation_id );
						if ( $variation instanceof \WC_Product && $variation->get_parent_id() > 0 ) {
							$product_id = $variation->get_parent_id();
						}
					}
					if ( $product_id <= 0 ) {
						continue;
					}

					$terms = \get_the_terms( $product_id, 'product_cat' );
					if ( ! is_array( $terms ) ) {
						continue;
					}
					foreach ( $terms as $term ) {
						$raw[] = (string) $term->slug;
						$raw[] = (string) $term->name;
					}
				}
				break;

			default:
				return array();
		}

		return self::normalize_list( $raw );
	}

	/**
	 * Valora las reglas de un local sobre el pedido: todas deben cumplirse.
	 * Carga los hechos del pedido que piden las reglas y delega en matches_facts().
	 *
	 * @param \WC_Order         $order Pedido.
	 * @param array<int, mixed> $rules Reglas decodificadas del JSON.
	 */
	public static function order_matches( \WC_Order $order, array $rules ): bool {
		$facts = array();

		foreach ( $rules as $rule ) {
			if ( ! is_array( $rule ) ) {
				return false;
			}

			$type = (string) ( $rule['type'] ?? '' );

			if ( 'order_meta' === $type ) {
				$key = self::meta_key_of( $rule );
				if ( '' === $key ) {
					return false;
				}
				// HPOS: siempre por la API CRUD del pedido.
				$facts['meta'][ $key ] = self::normalize( (string) $order->get_meta( $key, true ) );
				continue;
			}

			if ( ! in_array( $type, self::TYPES, true ) ) {
				return false;
			}

			if ( ! array_key_exists( $type, $facts ) ) {
				$facts[ $type ] = self::order_values( $order, $type );
			}
		}

		return self::matches_facts( $facts, $rules );
	}

	/**
	 * Lógica pura: ¿cumplen las reglas (AND) los hechos del pedido?
	 *
	 * @param array<string, array<string, string>|array<int, string>> $facts Hechos:
	 *   por cada tipo, lista de valores candidatos normalizados; y bajo la clave
	 *   'meta', un mapa clave => valor normalizado.
	 * @param array<int, mixed>                                       $rules Reglas del local.
	 */
	public static function matches_facts( array $facts, array $rules ): bool {
		if ( array() === $rules ) {
			return false; // Un local sin reglas solo puede ser el por defecto.
		}

		foreach ( $rules as $rule ) {
			if ( ! is_array( $rule ) || ! self::rule_on_facts( $facts, $rule ) ) {
				return false;
			}
		}

		return true;
	}

	/** Evalúa una regla concreta sobre los hechos. */
	private static function rule_on_facts( array $facts, array $rule ): bool {
		$type     = (string) ( $rule['type'] ?? '' );
		$operator = (string) ( $rule['operator'] ?? '' );
		$values   = self::normalize_list( (array) ( $rule['values'] ?? array() ) );

		if ( ! in_array( $type, self::TYPES, true ) || ! in_array( $operator, self::OPERATORS, true ) ) {
			return false;
		}

		if ( 'order_meta' === $type ) {
			$key = self::meta_key_of( $rule );
			if ( '' === $key || ! array_key_exists( $key, $facts['meta'] ?? array() ) ) {
				return false;
			}
			$value = (string) $facts['meta'][ $key ];

			return self::apply( $operator, '' === $value ? array() : array( $value ), $values );
		}

		return self::apply( $operator, (array) ( $facts[ $type ] ?? array() ), $values );
	}

	/** Clave de metadato de una regla order_meta (campo key o values[0]). */
	private static function meta_key_of( array $rule ): string {
		$values = (array) ( $rule['values'] ?? array() );

		return trim( (string) ( $rule['key'] ?? ( $values[0] ?? '' ) ) );
	}

	/**
	 * Aplica el operador a los valores del pedido contra los de la regla.
	 *
	 * @param array<int, string> $candidates  Valores del pedido (normalizados).
	 * @param array<int, string> $rule_values Valores de la regla (normalizados).
	 */
	private static function apply( string $operator, array $candidates, array $rule_values ): bool {
		$rule_values = array_values( array_filter( $rule_values, static fn( string $v ): bool => '' !== $v ) );

		switch ( $operator ) {
			case 'exists':
				return array() !== $candidates;

			case 'in':
				return array() !== $rule_values && array() !== array_intersect( $candidates, $rule_values );

			case 'equals':
				return 1 === count( $rule_values ) && array() !== array_intersect( $candidates, $rule_values );

			case 'contains':
			case 'prefix':
				if ( array() === $rule_values ) {
					return false;
				}
				foreach ( $rule_values as $needle ) {
					foreach ( $candidates as $candidate ) {
						if ( 'contains' === $operator && str_contains( $candidate, $needle ) ) {
							return true;
						}
						if ( 'prefix' === $operator && str_starts_with( $candidate, $needle ) ) {
							return true;
						}
					}
				}
				return false;

			default:
				return false;
		}
	}

	/**
	 * Normalización de texto para matching libre: minúsculas y sin acentos.
	 * Usa remove_accents() de WordPress (que también lleva «ñ» a «n»: el
	 * matching es «A Coruña» ≡ «coruna»).
	 */
	public static function normalize( string $value ): string {
		$value = \remove_accents( trim( $value ) );
		return function_exists( 'mb_strtolower' )
			? mb_strtolower( $value, 'UTF-8' )
			: strtolower( $value );
	}

	/** Lista de valores normalizados y sin vacíos. */
	private static function normalize_list( array $raw ): array {
		$values = array();
		foreach ( $raw as $value ) {
			$value = trim( (string) $value );
			if ( '' !== $value ) {
				$values[] = self::normalize( $value );
			}
		}

		return array_values( array_unique( $values ) );
	}

	/**
	 * Valida y sanea el JSON de reglas recibido del formulario.
	 *
	 * @param string $json Texto del textarea.
	 * @return array{rules: array<int, array<string, mixed>>, error: string|null} Reglas saneadas o error.
	 */
	public static function sanitize_rules_json( string $json ): array {
		$json = trim( $json );
		if ( '' === $json ) {
			return array(
				'rules' => array(),
				'error' => null,
			);
		}

		$decoded = json_decode( $json, true );
		if ( ! is_array( $decoded ) ) {
			return array(
				'rules' => array(),
				'error' => 'invalid_json',
			);
		}

		if ( count( $decoded ) > self::MAX_RULES ) {
			return array(
				'rules' => array(),
				'error' => 'too_many_rules',
			);
		}

		$rules = array();
		foreach ( $decoded as $rule ) {
			if ( ! is_array( $rule ) ) {
				return array(
					'rules' => array(),
					'error' => 'invalid_rule',
				);
			}

			$type     = (string) ( $rule['type'] ?? '' );
			$operator = (string) ( $rule['operator'] ?? '' );
			$key      = (string) ( $rule['key'] ?? '' );

			if ( ! in_array( $type, self::TYPES, true ) || ! in_array( $operator, self::OPERATORS, true ) ) {
				return array(
					'rules' => array(),
					'error' => 'invalid_rule',
				);
			}

			$values = array();
			foreach ( (array) ( $rule['values'] ?? array() ) as $value ) {
				$value = trim( sanitize_text_field( (string) $value ) );
				if ( '' !== $value ) {
					$values[] = $value;
				}
			}

			if ( count( $values ) > self::MAX_VALUES ) {
				return array(
					'rules' => array(),
					'error' => 'invalid_rule',
				);
			}

			if ( 'order_meta' === $type && '' === $key ) {
				$key = $values[0] ?? '';
			}

			$clean = array(
				'type'     => $type,
				'operator' => $operator,
				'values'   => $values,
			);

			if ( 'order_meta' === $type ) {
				// La clave de metadato puede llevar guiones bajos («_shipping_zona»).
				$clean['key'] = sanitize_text_field( $key );
			}

			$rules[] = $clean;
		}

		return array(
			'rules' => $rules,
			'error' => null,
		);
	}
}
