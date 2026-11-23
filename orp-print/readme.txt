=== Orvita Print ===
Contributors: orvita
Tags: woocommerce, print, receipt, thermal-printer, pos
Requires at least: 6.8
Tested up to: 6.8
Requires PHP: 8.1
Stable tag: 1.0.0
License: GPLv2 or later
License URI: https://www.gnu.org/licenses/gpl-2.0.html

Convierte pedidos de WooCommerce en tickets para impresora térmica: locales y reglas por local, plantilla por campos, vista previa imprimible y cola de impresión con agente opcional para Windows.

== Description ==

Orvita Print añade a WooCommerce la impresión de tickets en locales físicos (hostelería y comercio): cada pedido se convierte en un documento neutro y se encola para el local que corresponda.

**Qué incluye el plugin (gratuito, sin ninguna función de pago bloqueada):**

* **Locales y reglas** — alta de locales (tiendas, barras, cocinas) con reglas por ciudad, código postal, método de envío, categoría de producto o metadato del pedido; prioridad y local por defecto con aviso.
* **Plantilla del ticket por campos** — activa o desactiva logo, datos del cliente, líneas, totales, QR y pie; elige ancho de papel (80/58 mm) y codificación (CP850/CP858/CP437).
* **Vista previa imprimible** — el ticket se ve y se imprime desde el navegador a 80/58 mm con «Imprimir», sin agente y sin crear trabajo. El plugin es útil desde el primer día.
* **Cola de trabajos con estados** — pendiente, reclamado, entregado, fallido, caducado y anulado, con cancelar y reencolar, caducidad configurable y protección contra duplicados.
* **Reimpresión desde el pedido** — botón «Ver ticket» y acción «Reimprimir ticket» en la ficha del pedido.
* **API REST propia** — contrato público `orp-print/v1` con reclamo atómico (dos agentes nunca imprimen el mismo trabajo), reconocimiento idempotente y límite de peticiones.
* **Compatible con HPOS** — declarado; los pedidos se leen siempre por la API CRUD de WooCommerce.
* **Registro técnico sin datos personales** y página de diagnóstico con comprobaciones automáticas.
* **Emparejamiento del agente con código de 6 dígitos** (caduca en 10 minutos, 5 intentos) y agentes por local con salidas (impresoras y copias).

**Servicio de pago opcional (agente de Windows):**

La impresión automática en la impresora térmica requiere el **agente de Orvita Print para Windows**, un software propietario que se instala en el PC del TPV, recoge los trabajos de su local vía HTTPS y los imprime en la impresora ya instalada. Es de pago único (sin suscripción): **79 €** (1 local), **279 €** (10 locales) e **ilimitado + white-label 549 €**, IVA no incluido, con actualizaciones incluidas 12 meses.

* Precios: https://orvitaprint.example/precios
* Términos de uso: https://orvitaprint.example/terminos

El plugin NO necesita el agente para nada de lo anterior: configurar locales, diseñar la plantilla, previsualizar e imprimir a mano es gratis y siempre lo será. No hay comprobación de licencia en este plugin ni llamadas a servidores de Orvita.

== Installation ==

1. Sube la carpeta `orp-print` a `/wp-content/plugins/` o instálala desde «Plugins → Añadir nuevo».
2. Activa el plugin. Se crean las tablas propias (prefijo `orp_`) y la capacidad `manage_orp_print` para el rol administrator.
3. Ve a **Orvita Print → Ajustes** y elige un preset (el de una instalación nueva es «Aviso»).
4. Ve a **Orvita Print → Locales y reglas** y crea al menos un local con sus reglas.
5. Diseña el ticket en **Plantilla del ticket** y compruébalo en **Vista previa**.
6. (Opcional) Para imprimir automáticamente, compra el agente de Windows, instálalo en el TPV y genera un código de emparejamiento en «Locales y reglas».

== Frequently Asked Questions ==

= ¿Necesito el agente de pago para usar el plugin? =

No. Sin agente puedes configurar locales y reglas, diseñar la plantilla, previsualizar el ticket e imprimirlo desde el navegador. El agente de Windows solo añade la impresión automática en la térmica del TPV.

= ¿Qué es exactamente lo que se paga? =

El agente de Windows (software propietario, licencia perpetua, pago único): impresión automática, servicio de Windows, reintentos, actualizaciones 12 meses y soporte. Los precios y los términos están enlazados en la descripción. Este plugin (GPL) no bloquea ninguna función ni comprueba licencias.

= ¿Funciona con el checkout de bloques y con el clásico? =

Sí: se engancha a la creación del pedido y a los dos checkouts; la deduplicación garantiza exactamente un trabajo por pedido (o por disparo, si lo cambias en Ajustes).

= ¿Es compatible con HPOS? =

Sí, la compatibilidad está declarada y todos los accesos al pedido usan la API CRUD de WooCommerce.

= ¿Un pedido sin local coincidente qué hace? =

Va al local por defecto si hay uno y se muestra un aviso en el panel; si no hay local por defecto, el pedido no se encola y el aviso lo indica. Los trabajos ya encolados no cambian si editas las reglas después.

= ¿Qué impresoras soporta? =

El agente imprime en las térmicas de TPV habituales (Epson, Xprinter…) mediante el spooler de Windows, con respaldo GDI y TCP 9100. Sin agente, cualquier impresora del navegador sirve para la impresión manual.

== Screenshots ==

1. Ajustes en tres capas: producto, preset y valores de instancia.
2. Locales y reglas de asignación con código de emparejamiento del agente.
3. Plantilla del ticket por campos con registro de cambios.
4. Vista previa imprimible del ticket a 80 mm.
5. Cola de trabajos con estados, agentes y acciones de cancelar/reencolar.
6. Página de estado con comprobaciones automáticas de diagnóstico.

== Changelog ==

= 1.0.0 =

Primera versión pública.

* Locales con reglas de asignación (ciudad, código postal, método de envío, categoría, meta) y local por defecto con avisos.
* Plantilla del ticket por campos con vista previa imprimible (80/58 mm) e impresión manual desde el navegador.
* Cola de trabajos con estados, reclamo atómico, reconocimiento idempotente, caducidad, retención y reimpresión desde el pedido.
* Emparejamiento del agente con código de 6 dígitos (10 min, 5 intentos) y throttling por IP.
* API REST `orp-print/v1`: health, jobs (poll/claim/ack/release), heartbeat, config y pair.
* HPOS declarado, rate limit por token (120/min con Retry-After), registro sin datos personales y página de diagnóstico.

== Upgrade Notice ==

= 1.0.0 =

Primera versión pública.
