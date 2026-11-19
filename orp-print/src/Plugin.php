<?php
/**
 * Núcleo del plugin: autoloader PSR-4 propio sobre src/ y arranque.
 */

declare(strict_types=1);

namespace Orvita\PrintBridge;

// Si se llama directamente, salir.
if ( ! defined( 'ABSPATH' ) ) {
	exit;
}

final class Plugin {

	/** Prefijo de namespace que mapea a la carpeta src/. */
	private const NAMESPACE_PREFIX = 'Orvita\\PrintBridge\\';

	/**
	 * Registra el autoloader. Se llama desde el fichero principal, antes de
	 * cualquier clase, para que también esté disponible en el hook de activación.
	 */
	public static function register_autoloader(): void {
		spl_autoload_register( array( self::class, 'autoload' ) );
	}

	/**
	 * Carga una clase del namespace Orvita\PrintBridge desde src/.
	 *
	 * @param string $class Nombre completo de la clase.
	 */
	public static function autoload( string $class ): void {
		if ( ! str_starts_with( $class, self::NAMESPACE_PREFIX ) ) {
			return;
		}

		$relative = substr( $class, strlen( self::NAMESPACE_PREFIX ) );
		$file     = ORP_PATH . 'src/' . str_replace( '\\', '/', $relative ) . '.php';

		if ( is_file( $file ) ) {
			require_once $file;
		}
	}

	/** Arranque en plugins_loaded. */
	public static function boot(): void {
		add_action( 'init', array( self::class, 'load_textdomain' ) );

		// API REST (Doc 04 §5): salud, trabajos, heartbeat y configuración.
		Rest\Router::register();

		// Panel de administración mínimo (el completo llega en F1-01).
		Admin\Menu::register();

		// Comando de siembra para F0: `wp orp seed` (F0-08).
		if ( defined( 'WP_CLI' ) && \WP_CLI ) {
			\WP_CLI::add_command( 'orp seed', array( Cli\SeedCommand::class, 'run' ) );
		}
	}

	/** Carga del text domain. */
	public static function load_textdomain(): void {
		load_plugin_textdomain(
			'orp-print',
			false,
			dirname( plugin_basename( ORP_FILE ) ) . '/languages'
		);
	}
}
