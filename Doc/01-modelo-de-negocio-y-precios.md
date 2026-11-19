# Modelo de negocio y hoja de precios

> Orvita Print · v1.0 · Noviembre 2026

---

## 1. Quién compra y quién usa

| Rol | Quién es | Qué le importa | Qué paga |
|---|---|---|---|
| **Comprador** | Desarrollador, agencia o freelance que monta el WordPress del restaurante | No construir ni mantener un agente de escritorio; no añadir coste recurrente al cliente | Licencia del agente |
| **Usuario** | Personal del restaurante en el TPV | Que el ticket salga solo y que nadie tenga que tocar nada | Nada |
| **Beneficiario** | Dueño del restaurante | No perder pedidos | Nada (lo paga dentro del proyecto) |

Consecuencia de diseño: **el producto se vende al técnico**. La documentación, el instalador y los diagnósticos se escriben para él, no para el hostelero. El hostelero solo ve el papel.

---

## 2. Posicionamiento frente a la competencia

Datos revisados en noviembre de 2026. Los precios pueden cambiar.

| Competidor | Modelo | Precio | Dependencia |
|---|---|---|---|
| PrintNode | Suscripción | 7,20–79,20 €/mes | Servicio cloud |
| BizPrint | Suscripción | desde 3 USD/mes | Servicio cloud |
| The Auto Print | Freemium | 0 / 5 / 9 USD/mes | App + cloud |
| PrintTrigger | Freemium | 0 / 6 / 49 USD/mes | Agente + cloud |
| Printus | Plugin gratis | 0 € (+ PrintNode) | Servicio cloud |
| PrinterCo | **Pago único** | 39 £ / 169 £ lifetime | Terminal Android propio (venta de hardware) |

### Lectura

- **El pago único no es un diferenciador universal.** PrinterCo ya lo ofrece. Pero su precio está subvencionado por la venta de terminales: es coste de adquisición, no precio de software.
- **Nuestro hueco no es el precio, es la combinación:** pago único + sin hardware + sin nube + multi-local nativo + soporte en español.
- **No competimos con PrinterCo en precio.** Competimos en que el restaurante conserva su impresora y su PC.

> **Mensaje comercial:** «Sin hardware nuevo, sin cuota mensual, con la impresora que ya tienes.»

---

## 3. Estructura de producto

### Plugin (gratuito)

- Licencia GPL v2+.
- Destinado a WordPress.org y GitHub. La fecha de publicación no bloquea el piloto ni la primera venta.
- Sin versión "pro": todas las funciones del plugin van incluidas.
- **Es útil por sí solo**: sin agente se puede configurar el local, diseñar la plantilla, previsualizarla, imprimirla desde el navegador y reimprimir desde el pedido.
- Su función es la distribución y la confianza técnica (código auditable, actualizaciones, soporte en el repositorio).

### Agente (de pago)

- Binario propietario para Windows.
- Añade la **automatización**: servicio de Windows, impresión sin navegador ni intervención, reintentos, spooler, actualización automática y soporte.
- Es el único componente que no funciona sin licencia.
- La licencia se valida de forma local con un fichero firmado, para que el sistema no dependa de nuestra infraestructura.

| Función | Plugin gratis | Agente de pago |
|---|---|---|
| Locales y reglas de asignación | Sí | — |
| Plantilla por campos | Sí | — |
| Vista previa del ticket | Sí | — |
| Imprimir desde el navegador | Sí | — |
| Reimpresión manual desde el pedido | Sí | — |
| Cola, estados y registro | Sí | — |
| **Impresión automática en térmica** | No | Sí |
| Servicio de Windows sin sesión | No | Sí |
| Reintentos y spooler | No | Sí |
| Actualizaciones y soporte | — | Sí |

Esto resuelve la contradicción de GPL: **el plugin se puede copiar, el agente no sirve sin licencia.** El foso no es el código PHP, es el binario y el soporte.

### Nombre y promesa

El plugin gratuito **no puede llamarse «impresión automática»**, porque sin agente no imprime solo. El nombre debe describir lo que hace gratis: configurar, previsualizar e imprimir tickets.

- Plugin: nombre del tipo **«Order Ticket Preview & Print for WooCommerce»** (pendiente de decisión final).
- Agente: nombre propio que sí puede prometer automatización.

Esta separación no es marketing: es lo que hace honesta la ficha de WordPress.org y evita que un revisor la lea como un embudo.

### Reglas de WordPress.org

| Regla | Qué exige | Cómo se cumple |
|---|---|---|
| Guideline 5 · sin trialware | No bloquear funciones a la espera de pago | El plugin no tiene funciones bloqueadas, cuotas ni caducidad |
| Guideline 6 · serviceware | El servicio externo debe aportar funcionalidad real y documentarse en el readme | El agente imprime de verdad; se declara con enlace a precios y términos |
| Sin validador de licencias | Un servicio que solo comprueba claves no se admite | El plugin no valida licencias ni llama a Orvita |
| Código completo | Todo el PHP distribuido es GPL y auditable | Sin ofuscación ni código premium dentro del plugin |

**Texto obligatorio en el readme** (sección «Servicio opcional de pago»):

> La impresión automática en impresora térmica requiere un agente independiente para Windows, disponible en [URL] previo pago. El plugin funciona sin él para configurar locales, diseñar la plantilla, previsualizar el ticket e imprimir manualmente desde el navegador. Precios y condiciones: [URL].

### Qué se puede y qué no se puede vender

- Se puede vender el **conjunto** plugin + agente, pero el plugin es GPL y el comprador puede redistribuirlo. No se pierde nada real: sin agente no imprime.
- **No se pone DRM ni comprobación de licencia en el plugin.** Además de incumplir el espíritu de WordPress.org, sería inútil.
- El protocolo es visible en el plugin; un competidor podría escribir su propio agente. Es el coste del modo directo y de la GPL: se compite en calidad y soporte, no en secreto.
- WordPress.org **no vende nada**. El cobro va por nuestra web con un *Merchant of Record*; el directorio solo distribuye el plugin gratis.

---

## 4. Hoja de precios

IVA no incluido. Precios en euros.

| SKU | Límite | Precio | Actualizaciones | Soporte | White-label |
|---|---|---|---|---|---|
| **Personal** | 1 local | **79 €** | 12 meses | 12 meses | No |
| **Estudio** | 10 locales | **279 €** | 12 meses | 12 meses | No |
| **Ilimitado** | Sin límite | **549 €** | 12 meses | 12 meses | Sí |

**Renovación de actualizaciones** (opcional, tras los 12 meses):

| SKU | Renovación anual |
|---|---|
| Personal | 32 € |
| Estudio | 112 € |
| Ilimitado | 220 € |

Si no se renueva, **el software sigue funcionando indefinidamente** en la última versión descargada. Solo se deja de recibir actualizaciones y soporte.

### Precio por local (referencia interna)

| SKU | €/local |
|---|---|
| Personal | 79,00 |
| Estudio | 27,90 |
| Ilimitado | decreciente |

El escalón de 10 locales está pensado para que sea la opción evidente: un estudio con tres clientes de dos locales cada uno ya lo amortiza.

### Licencia interna

La pizzería piloto recibe una licencia **Ilimitada interna, sin coste y sin caducidad**. Se documenta como caso de referencia y no se factura.

---

## 5. Qué incluye y qué no

### Incluye

- Plugin con todas las funciones.
- **Salidas múltiples**: varias impresoras por local o varias copias en la misma.
- Agente para Windows 10/11 x64.
- Instalador para Windows con hash de descarga publicado.
- Actualizaciones durante 12 meses.
- Soporte por email/ticket, respuesta objetivo 48 h laborables.
- Documentación de instalación y administración.

### No incluye

- Instalación o configuración de impresoras y drivers.
- Soporte telefónico.
- Instalación en el servidor del cliente.
- Desarrollo a medida (se presupuesta aparte).
- Versiones para macOS, Linux, Android o iOS.
- Servicio cloud / relay.
- Migración desde otro plugin de impresión (se presupuesta aparte).

### Cláusula de soporte (texto para la web)

> El agente imprime en las impresoras ya instaladas en Windows. No instala drivers ni configura impresoras. Si la impresora no aparece en la lista del agente, debe instalarla el proveedor del equipo. El soporte cubre el plugin, el agente y la comunicación entre ambos.

---

## 6. Prueba y devoluciones

- **Prueba:** 30 días completos del **agente**, 1 local, sin marca de agua en el ticket. Al terminar, el agente deja de procesar trabajos hasta activar licencia. El plugin sigue siendo gratuito e ilimitado; lo que caduca es la automatización.
- **Devolución:** 14 días desde la compra si la licencia no se ha activado en ningún equipo.
- **Cambio de equipo:** la licencia se puede reasignar; se permite desactivar y reactivar sin coste.

Racional de la prueba: el comprador es técnico y quiere ver el ticket real antes de comprometerse. Una demo no convierte; un ticket impreso sí.

---

## 7. Canales

| Canal | Mecánica | Esfuerzo | Prioridad |
|---|---|---|---|
| **Directo** | Web propia + agente de pago tras el cobro; el plugin gratis en WordPress.org como escaparate | Medio | 1 |
| **WordPress.org** | Canal de distribución y confianza, no de venta. **No es dependencia de lanzamiento** | Bajo | 1 |
| **Themes** | El theme incluye el plugin gratis; el cliente final compra el agente; el autor del theme recibe 25 % de comisión | Bajo | 2 |
| **Agencias** | Licencia Ilimitada + white-label; se factura al estudio | Bajo | 2 |
| **Marketplace WooCommerce** | Excluido: exige GPL en todo lo listado y limita los enlaces de venta externos | — | No |

### Sobre WordPress.org

Publicar en el directorio es deseable, pero **no es una puerta de lanzamiento**. La revisión tarda semanas, es subjetiva y puede pedir cambios. Mientras tanto:

- El piloto no la necesita: el plugin se instala desde ZIP o desde el repositorio propio.
- La primera venta tampoco: el agente se vende en nuestra web.
- Si la revisión se rechaza, el producto sigue vivo; solo se pierde el escaparate.

Por eso el orden es: construir el plugin autónomo → piloto → publicación. Nunca al revés.

Riesgo asumido: si el revisor considera que el plugin solo existe para vender el agente, lo rechaza. La mitigación es la impresión manual desde el navegador y el readme con la sección de servicio; ambas son funciones reales, no un argumento de defensa.

### Sobre el canal de themes

Funciona con **licencia Extended** si el theme se vende en Envato: se paga una vez por theme, no por instalación, y el comprador del theme no recibe soporte ni actualizaciones del autor del plugin.

No se plantea como línea de negocio sino como canal: hay pocos autores de themes de restaurante y el comprador del theme suele ser poco técnico, lo que devuelve el problema de soporte. La estructura recomendada (plugin gratis + agente de pago + comisión) evita ese problema porque solo recibe soporte quien paga.

---

## 8. Métricas de seguimiento

| Métrica | Objetivo primer año |
|---|---|
| Instalaciones activas del plugin | 300 |
| Conversiones a licencia | 5 % de instalaciones activas |
| Licencias vendidas | 15 |
| Ingreso medio por licencia | 250 € |
| Tickets de soporte / licencia / mes | < 0,15 |
| Tiempo medio de resolución | < 24 h |

Si a los 6 meses no hay **3 licencias de pago**, la decisión es mantenerlo como herramienta interna y caso de portfolio, no seguir invirtiendo en comercialización.

---

## 9. Riesgos comerciales

| Riesgo | Señal de alarma | Respuesta |
|---|---|---|
| PrinterCo baja a 99 £ | Comparativas en foros | Reforzar multi-local y soporte ES; no bajar precio |
| WooCommerce sigue cayendo | Menos instalaciones del plugin | Abrir canal Shopify con relay |
| Canibalización por IA | Los agentes instalan alternativas gratis | Publicar `llms.txt` y documentación para agentes; el agente sigue siendo de pago |
| Soporte desbordado | > 0,3 tickets/licencia/mes | Mejorar diagnóstico automático antes de contratar |
