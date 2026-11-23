<?php
/**
 * Sonda del humo E2E (destino con address como printer_name): destinos nuevos
 * y últimos warnings de agents en orp_logs (sin strict_types: wp eval-file la
 * exige como primera sentencia absoluta del script).
 *
 * Uso interno: wp eval-file wp-content/plugins/orp-print/tools/_t_e2e_dest_probe.php
 */

global $wpdb;

echo "== DESTINOS (address / rechazados) ==\n";
// phpcs:ignore WordPress.DB.PreparedSQL.NotPrepared, WordPress.DB.DirectDatabaseQuery
$rows = $wpdb->get_results(
	"SELECT role, label, printer_name, copies, enabled
	   FROM {$wpdb->prefix}orp_destinations
	  WHERE printer_name LIKE '192.168%' OR role IN ('sin-nada', 'solo-rol')
	  ORDER BY id ASC"
);
foreach ( (array) $rows as $row ) {
	printf(
		"role=%s | label=%s | printer_name=%s | copies=%d | enabled=%d\n",
		$row->role,
		$row->label,
		$row->printer_name,
		(int) $row->copies,
		(int) $row->enabled
	);
}

echo "== LOGS (warning/agents) ==\n";
// phpcs:ignore WordPress.DB.PreparedSQL.NotPrepared, WordPress.DB.DirectDatabaseQuery
$logs = $wpdb->get_results(
	"SELECT level, context, agent_id, message, data
	   FROM {$wpdb->prefix}orp_logs
	  WHERE level = 'warning' AND context = 'agents'
	  ORDER BY id DESC
	  LIMIT 3"
);
foreach ( (array) $logs as $log ) {
	printf(
		"%s/%s agent=%d | %s | %s\n",
		$log->level,
		$log->context,
		(int) $log->agent_id,
		$log->message,
		(string) $log->data
	);
}
