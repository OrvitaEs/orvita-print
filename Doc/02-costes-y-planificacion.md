# Costes, esfuerzo y planificación económica

> Orvita Print · v1.1 · Noviembre 2026
> **Revisión:** la estimación inicial (320 h) era una estimación de agencia tradicional. Con desarrollo asistido por IA baja a **190–215 h**, pero no baja a cero. Este documento explica por qué y dónde está el suelo.

---

## 1. Resumen

| Escenario | Horas de trabajo | Calendario real | Caja |
|---|---|---|---|
| Estimación clásica (agencia) | 332 h | 10 semanas | 11.600–16.600 € |
| **Desarrollo asistido (nosotros)** | **190–215 h** | **5 semanas de trabajo + 30 días de piloto** | **≈ 0 €** |
| Solo MVP (no comercializable) | 140–160 h | 3–4 semanas | ≈ 0 € |

El calendario hasta poder vender **nunca baja de 9–10 semanas**, aunque el código esté en cinco. La razón está en la sección 4.

---

## 2. Por qué el código se comprime y el hardware no

Una refactorización de tokens a Tailwind se hace en una hora porque el objetivo está completamente especificado y la verificación es instantánea: compila o no compila, se ve o no se ve. La IA es demoledoramente rápida ahí.

Aquí la mitad de la especificación es **"que el papel salga bien"**, y eso solo se comprueba imprimiendo.

| Tipo de tarea | Factor de compresión con IA | Motivo |
|---|---|---|
| CRUD, formularios de admin, SQL, controladores REST | **4–6×** | Patrones conocidos, verificación instantánea |
| Plantillas, renderizado, configuración, instaladores | **3–4×** | Código determinista, errores visibles al compilar |
| Concurrencia, idempotencia, seguridad | **1,5–2×** | La IA lo escribe; hay que leerlo y probarlo |
| Integración con impresoras reales | **1,2–1,5×** | El feedback es físico y lento |
| Pruebas en TPV reales, drivers, sesión 0 | **1×** | Tiempo de pared |
| Piloto en producción | **1×** | 30 días son 30 días |

**Conclusión:** la IA elimina el trabajo de tecleo, no el de verificación física. En este proyecto el tecleo era aproximadamente la mitad.

---

## 3. Desglose revisado

### Plugin WordPress — 70 h

| Tarea | Clásica | Asistida |
|---|---|---|
| Estructura, ajustes, HPOS, i18n | 10 | 5 |
| Locales y reglas de asignación | 18 | 8 |
| Hooks, creación de trabajos, idempotencia | 12 | 6 |
| Plantilla, vista previa, impresión manual, documento neutro | 22 | 10 |
| Tabla + REST (pair, heartbeat, jobs, claim, ack) | 22 | 10 |
| Panel de administración | 24 | 14 |
| Salidas por local y fan-out de trabajos | 12 | 12 |
| Seguridad, tokens, rate limit | 8 | 5 |
| Limpieza, migraciones, desinstalación | 4 | 3 |
| Colchón de bugs | — | 9 |
| **Total** | **132** | **82** |

### Agente Windows — 78 h

| Tarea | Clásica | Asistida |
|---|---|---|
| Solución, configuración, logging, servicio | 10 | 5 |
| Cliente HTTP, autenticación, reintentos | 12 | 5 |
| Cola local SQLite y recuperación | 10 | 5 |
| Render ESC/POS (bloques, codificaciones, corte, logo) | 26 | 14 |
| Spooler RAW, fallback GDI, 9100 | 18 | 10 |
| Enumeración de impresoras, prueba, estado | 10 | 6 |
| Servicio + CLI + pipe de gestión | 22 | 12 |
| Actualización automática firmada | 14 | 7 |
| Instalador MSI y empaquetado | 12 | 6 |
| Diagnóstico y exportación de registros | 6 | 3 |
| Colchón de bugs | — | 5 |
| **Total** | **140** | **78** |

### Transversal, QA y entrega — 45 h

| Tarea | Clásica | Asistida |
|---|---|---|
| Contrato, simulador y colección de pruebas | 10 | 5 |
| QA funcional y matriz de impresoras | 24 | 22 |
| Documentación | 12 | 4 |
| Empaquetado, release, generación de licencias | 8 | 6 |
| Despliegue en los tres TPV | 6 | 8 |
| **Total** | **60** | **45** |

**Total V1 asistida: 205 h.** Rango defendible: **190–215 h.**

---

## 4. Lo irreducible (≈ 55 h)

Estas horas no las toca la IA porque dependen del mundo físico o de plazos externos:

| Bloque | Horas | Por qué no se comprime |
|---|---|---|
| Matriz de impresoras: papel, acentos, corte, cajón | 20 | Hay que imprimir, mirar y repetir |
| Servicio Windows + sesión 0 + drivers en los tres TPV | 12 | Depende de cada equipo |
| Pruebas de caída: luz, red, impresora apagada, lease | 8 | Los tiempos de espera son reales |
| Instalador en máquina limpia + SmartScreen | 6 | Requiere instalación y desinstalación real |
| Verificación de concurrencia y seguridad | 5 | Hay que leer el código generado |
| Despliegue en producción | 4 | Presencial o remoto con el cliente |
| **Subtotal** | **55** | |

Y aparte, **30 días de piloto** que son calendario, no trabajo. Ahí no hay atajo posible: es el tiempo que tarda en aparecer el fallo raro.

---

## 5. Coste

| Escenario | Cálculo | Coste |
|---|---|---|
| Lo desarrollamos nosotros | 205 h propias | 0 € de caja |
| Externalizado con IA, 40 €/h | 205 × 40 | 8.200 € |
| Externalizado clásico, 40 €/h | 332 × 40 | 13.280 € |
| Mixto: nosotros el plugin, externo el agente | 78 × 45 | 3.510 € |

La diferencia entre 205 y 332 h son unos **5.100 €** o **cuatro semanas**. Es real, pero no es el factor 10 que sugiere la anécdota de Tailwind.

---

## 6. Costes recurrentes

El mínimo real es casi cero si se apoya en infraestructura existente y en un *Merchant of Record*. La cifra de 130–320 €/año era un envoltorio conservador, no una obligación.

| Concepto | Mínimo | Cómodo | Por qué |
|---|---|---|---|
| Dominio | 0 € | 12–20 € | Un subdominio de Orvita (`print.orvita.com`) no cuesta; un dominio nuevo sí |
| Landing y descarga | 0 € | 0–60 € | Cloudflare Pages/R2 o GitHub Releases; el MSI pesa poco |
| Servidor de actualizaciones | 0 € | incluido | Un `latest.json` estático en el mismo hosting |
| Cobro y facturas | 0 € fijo | 0 € fijo | Lemon Squeezy, Paddle o Gumroad: comisión por venta e IVA incluido |
| Email transaccional | 0 € | 0–60 € | El MoR envía el recibo; el soporte usa el correo actual |
| Firma de código | 0 € | 0 € | No en V1 |
| **Total** | **0–20 €/año** | **≈ 130–320 €/año** | |

### Coste variable, no anual

| Concepto | Coste |
|---|---|
| Comisión de la plataforma de pago | 3–10 % por venta según el MoR |
| Pasarela directa (Stripe) | ~1,5 % + 0,25 €; obliga a gestionar IVA/OSS |
| Devoluciones y contracargos | Según volumen |

Recomendación para arrancar: subdominio del dominio actual, Cloudflare Pages/R2 para el MSI y el manifiesto, y un MoR para cobrar. Coste fijo cercano a cero; el variable solo aparece cuando hay ventas.

No se firma el instalador en V1: se descarga solo desde nuestra web tras el pago y lo instala un profesional. Se revisa si el volumen o el soporte lo justifican.

| Único | Coste |
|---|---|
| Términos, privacidad, plantilla de DPA | 300–800 € |
| Registro de marca (opcional) | 150–300 € |

Sin nube en V1: no hay coste de infraestructura de impresión.

---

## 7. Punto de equilibrio

| Escenario | Licencias para equilibrar |
|---|---|
| Desarrollo propio (0 € de caja) | **1–2** (según infraestructura) |
| Externalizado 8.200 € a 279 € | **29** |
| Externalizado clásico 13.280 € a 279 € | **48** |

Con la conversión objetivo (15 licencias el primer año), **externalizar sigue sin recuperarse antes del año 2–3**. Desarrollarlo nosotros lo hace rentable desde el segundo cliente.

---

## 8. Calendario

| Fase | Trabajo | Calendario |
|---|---|---|
| F0 · Contrato y simulador | 5 h | 2 días |
| F1 · Plugin | 82 h | 1 semana |
| F2 · Agente e impresión | 78 h | 1,5 semanas |
| F3 · Instalador y licencia | 20 h | 3 días |
| F4 · QA y despliegue piloto | 20 h | 1 semana |
| **Piloto en producción** | 0 h | **30 días** |
| F5 · Empaquetado comercial | 10 h | 3 días |

| Dedicación | Trabajo | Hasta comercializar |
|---|---|---|
| Jornada completa | 5 semanas | **9–10 semanas** |
| 3 días/semana | 8 semanas | **12–13 semanas** |
| 2 días/semana | 12 semanas | **16–17 semanas** |

---

## 9. Decisiones del socio

1. **¿Lo hacemos nosotros?** Con estos números, externalizar no tiene sentido económico.
2. **¿Se factura el piloto?** Recomendación: sí, 1.500–2.500 € como proyecto de implantación.
3. **¿Se recorta a MVP?** No: ahorra 50 h y obliga a rehacer la mitad del agente.
4. **¿Cuándo se empieza el reloj del piloto?** El día que los tres TPV imprimen con la versión candidata. Todo lo anterior es preparación.
