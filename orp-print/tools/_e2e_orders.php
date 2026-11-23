<?php
/**
 * E2E: crea dos pedidos reales de pizzería en WooCommerce con shipping_city
 * «Madrid» (local-centro) para que el disparo genere trabajos.
 * Uso: wp eval-file /var/www/html/wp-content/plugins/orp-print/tools/_e2e_orders.php
 */

global $wpdb;

// Productos (crear si no existen).
function orp_e2e_product( string $name, string $price ): int {
	$existing = get_posts(
		array(
			'post_type'      => 'product',
			'title'          => $name,
			'posts_per_page' => 1,
			'fields'         => 'ids',
		)
	);
	if ( ! empty( $existing ) ) {
		return (int) $existing[0];
	}

	$p = new WC_Product_Simple();
	$p->set_name( $name );
	$p->set_regular_price( $price );
	$p->set_status( 'publish' );
	$p->save();

	return $p->get_id();
}

$margarita = orp_e2e_product( 'Pizza Margarita grande', '12.00' );
$barbacoa  =orp_e2e_product( 'Pizza Barbacoa', '11.00' );
$tarta     = orp_e2e_product( 'Tarta de queso', '4.50' );

// Pedido 1: María — una margarita, dos barbacoas, reparto.
$o1 = new WC_Order();
$o1->set_shipping_city( 'Madrid' );
$o1->set_billing_first_name( 'María' );
$o1->set_billing_last_name( 'López' );
$o1->set_billing_phone( '600 000 000' );
$o1->set_shipping_address_1( 'Calle Mayor 3, 2º B' );
$o1->add_product( wc_get_product( $margarita ), 1 );
$o1->add_product( wc_get_product( $barbacoa ), 2 );
$o1->calculate_totals();
$o1->set_status( 'pending' );
$o1->save();
echo 'ORDER1=' . $o1->get_id() . " total=" . $o1->get_total() . "\n";

// Pedido 2: Javier — dos margaritas y una tarta, recogida en local.
$o2 = new WC_Order();
$o2->set_shipping_city( 'Madrid' );
$o2->set_billing_first_name( 'Javier' );
$o2->set_billing_last_name( 'Fernández' );
$o2->set_billing_phone( '655 111 222' );
$o2->set_shipping_address_1( 'Recogida en local — Plaza del Sol 8' );
$o2->add_product( wc_get_product( $margarita ), 2 );
$o2->add_product( wc_get_product( $tarta ), 1 );
$o2->calculate_totals();
$o2->set_status( 'pending' );
$o2->save();
echo 'ORDER2=' . $o2->get_id() . " total=" . $o2->get_total() . "\n";

// Estado de la cola para ambos.
$jobs = $wpdb->get_results(
	"SELECT id, order_id, printer_role, copies, status FROM {$wpdb->prefix}orp_jobs WHERE order_id IN ("
	. (int) $o1->get_id() . ',' . (int) $o2->get_id() . ') ORDER BY id'
);
echo "TRABAJOS:\n";
foreach ( $jobs as $j ) {
	echo "  job={$j->id} order={$j->order_id} role={$j->printer_role} copias={$j->copies} estado={$j->status}\n";
}
