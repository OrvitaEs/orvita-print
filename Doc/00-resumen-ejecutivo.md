# Orvita Print · Resumen ejecutivo

> Documento de trabajo para socios. Versión 1.0 · Noviembre 2026
> Nombre comercial pendiente de decisión. En código se usa el prefijo `orp`.

---

## Qué es

Un sistema de impresión de tickets para WooCommerce formado por dos piezas:

1. **Plugin de WordPress (gratuito, GPL).** Detecta el pedido, decide a qué local corresponde y lo encola. Sin agente ya es útil: configura locales y reglas, diseña la plantilla, previsualiza el ticket y lo imprime desde el navegador.
2. **Agente de Windows (propietario, de pago).** Se instala en el PC del TPV y añade la automatización: recoge los trabajos de su local y los imprime en la térmica ya instalada, sin navegador ni intervención.

Sustituye la cadena actual **email → Thunderbird → FiltaQuilla → PrintingTools NG** por una conexión directa entre la tienda y el PC del local.

---

## Por qué existe

El cliente inicial (pizzería con tres locales, 200–300 pedidos/mes) imprime hoy los tickets con una cadena de cuatro programas de terceros donde el enrutado por local vive en un filtro de correo, no en el pedido. Es frágil y no es vendible.

El sistema propio resuelve tres cosas que la competencia no resuelve bien:

- **El local es un dato del pedido**, no una deducción sobre un asunto de email.
- **Sin servicios de impresión de terceros** (PrintNode, PrintTrigger) ni coste por impresión.
- **Sin hardware nuevo**: usa la impresora que el restaurante ya tiene.

---

## A quién se vende

Al **desarrollador o agencia que monta el WordPress del restaurante**, no al dueño de la pizzería.

Ese comprador valora tres cosas: que no le añada coste recurrente al cliente, que no le obligue a construir y mantener un agente de escritorio, y que sea fácil de dejar configurado en una tarde.

---

## Modelo de negocio

**El plugin es gratuito y funciona solo. El agente es el producto y el metro: se paga por automatizar, no por desbloquear.**

| Concepto | Decisión |
|---|---|
| Licencia del plugin | GPL v2+, gratuita, publicada en WordPress.org |
| Licencia del agente | Propietaria, **perpetua** |
| Qué gratis | Configurar locales y reglas, plantilla, vista previa, impresión manual desde el navegador |
| Qué de pago | Impresión automática, servicio de Windows, reintentos, actualizaciones y soporte |
| Actualizaciones | Incluidas 12 meses; después, opcionales |
| Soporte | Email/ticket, 48 h laborables. Sin teléfono |
| Modelo | Pago único, sin suscripción |
| Distribución | WordPress.org como canal, no como dependencia de lanzamiento |

Tres SKU: **79 €** (1 local), **279 €** (10 locales), **549 €** (ilimitado + white-label). IVA no incluido.

El detalle completo está en `01-modelo-de-negocio-y-precios.md`.

---

## Inversión y retorno

| Concepto | Cifra |
|---|---|
| Desarrollo V1 comercializable | **190–215 horas de trabajo** (≈ 5 semanas) |
| Hasta poder comercializar | **9–10 semanas** (5 de trabajo + 30 días de piloto) |
| Coste en horas propias | **≈ 0 € de caja** |
| Coste si se externaliza a 40–50 €/h | **8.200–10.300 €** |
| Costes recurrentes anuales | **≈ 0–20 €/año** mínimo · **130–320 €/año** con infraestructura propia |
| Punto de equilibrio | **1–2 licencias** (desarrollo propio) · **29** (externalizado) |

La pizzería inicial no paga licencia: se entrega como caso piloto y activo propio.

---

## Alcance de la V1

**Dentro:**

- Locales como entidad propia, con reglas de asignación (ciudad, código postal, método de envío, categoría de producto).
- Un ticket por pedido con **una o varias salidas**: varias impresoras o varias copias en la misma. Todas imprimen el mismo documento en V1.
- Plantilla por campos seleccionables, con **vista previa imprimible e impresión manual desde el navegador**, sin necesidad del agente.
- Cola de trabajos con estados, reintentos, caducidad y protección contra duplicados.
- Emparejamiento del agente con código de 6 dígitos.
- Agente como servicio headless gestionado por CLI (`orvitaprint`): emparejamiento, salidas, prueba, diagnóstico y registro exportable.
- Instalador con hash publicado y actualización automática.
- Compatibilidad HPOS.

**Fuera (por decisión):**

- Nube propia / relay (llega con otras plataformas).
- Instalación de drivers o configuración de impresoras: responsabilidad del cliente.
- Android / iOS.
- Multi-tenant, facturación automática, portal de cliente.
- Edición libre de plantillas tipo page builder.

---

## Riesgos principales

| Riesgo | Impacto | Mitigación |
|---|---|---|
| Codificación de acentos en ESC/POS | Alto (tickets ilegibles) | Prueba obligatoria con juegos de caracteres reales antes de entregar |
| SmartScreen bloquea el instalador | Medio | Instalación por un profesional + instrucciones con capturas; firma solo si el volumen lo justifica |
| Impresión desde servicio de Windows (sesión 0) | Medio | Prueba en los tres TPV reales; alternativa: tarea al inicio de sesión |
| Prometer actualizaciones perpetuas | Alto (quiebra lenta) | Cláusula explícita de 12 meses |
| Competidor con hardware subvencionado (PrinterCo) | Medio | No competir en precio; competir en multi-local y soporte en español |
| WordPress.org rechaza el plugin por parecer un embudo | Medio | El plugin imprime solo: vista previa e impresión manual; readme con la sección de servicio; WordPress.org no es dependencia de lanzamiento |

---

## Decisiones pendientes del socio

1. **Nombre comercial** del producto y del agente.
2. **Quién desarrolla**: socio, externo o mixto. Condiciona el calendario.
3. **Precio de lanzamiento**: mantener los tres SKU o añadir descuento de early adopter.
4. **Momento de publicación en WordPress.org**: al cerrar el piloto o antes. No bloquea el lanzamiento.
5. **Reparto de la licencia de la pizzería**: gratis indefinido o facturada como proyecto.

---

## Documentos

| Archivo | Contenido |
|---|---|
| `01-modelo-de-negocio-y-precios.md` | Comprador, competencia, SKU, cláusulas, canales |
| `02-costes-y-planificacion.md` | Desglose de horas, escenarios, costes recurrentes, equilibrio |
| `03-arquitectura-tecnica.md` | Componentes, decisiones, seguridad, despliegue |
| `04-modelo-de-datos-y-contrato.md` | Tablas SQL, endpoints REST, documento neutro |
| `05-plan-de-ejecucion-y-pruebas.md` | Fases, criterios de aceptación, matriz de pruebas |
| `06-guia-de-trabajo-f0.md` | Punto de entrada para programar: entorno, estructura, F0 y definición de hecho |
| `07-plan-tecnico-y-tareas.md` | Desglose operativo: tareas con ID, estimación, entregable y dependencias por fase |
| `08-gui-agente-avalonia.md` | Especificación de la GUI del agente (post-V1, activada por el piloto): pantallas, cola de trabajos, protocolo y tareas GUI-01..08 |
