# Plan de ejecución y pruebas

> Orvita Print · v1.0 · Noviembre 2026

---

## 1. Fases

| Fase | Duración | Objetivo | Entregable verificable |
|---|---|---|---|
| **F0** · Contrato | 2 días | Congelar la interfaz antes de escribir código | Tablas creadas, endpoints funcionando, simulador de agente |
| **F1** · Plugin | 1 semana | El pedido se convierte en trabajo | Alta de local, regla, plantilla, vista previa imprimible y trabajo en cola desde un pedido real |
| **F2** · Agente | 1,5 semanas | El trabajo se imprime | Ticket físico en las tres impresoras |
| **F3** · Licencia e instalación | 3 días | Se puede instalar sin nosotros | MSI, activación, actualización automática |
| **F4** · Piloto | 1 semana + 30 días | Funciona en producción | Tres TPV, 30 días de métricas |
| **F5** · Comercialización | 3 días | Se puede vender | Web y cobro, precios, readme con la sección de servicio, publicación en WordPress.org |

**Regla de secuencia:** no se empieza F5 hasta que F4 lleve **30 días consecutivos sin incidencias de impresión**.

El detalle operativo de F0 —entorno, estructura, tareas y definición de hecho— está en `06-guia-de-trabajo-f0.md`.

---

## 2. Hitos de control

| Hito | Criterio | Qué se decide |
|---|---|---|
| **H1** | El agente de referencia imprime correctamente en las tres impresoras | Continuar o replantear el transporte |
| **H2** | 100 pedidos de prueba sin duplicados ni pérdidas | Continuar con el panel de administración |
| **H3** | Corte de luz simulado sin pérdida de ticket | Validar la política de duplicado |
| **H4** | 30 días en producción sin incidencias | Autorizar la comercialización |

---

## 3. Criterios de aceptación

### Plugin

- [ ] Un pedido nuevo genera **exactamente un** trabajo.
- [ ] Con `dedupe_scope = trigger`, cambiar al estado configurado genera un trabajo nuevo; con `order`, no genera ninguno.
- [ ] Guardar la plantilla no duplica pedidos ya impresos.
- [ ] Un pedido sin local coincidente va al local por defecto y genera aviso.
- [ ] Funciona con HPOS activado y desactivado.
- [ ] Funciona con el checkout clásico y con el de bloques.
- [ ] La reimpresión manual desde la ficha del pedido genera un trabajo nuevo.
- [ ] La vista previa del ticket se abre **sin agente** y se imprime en 80 mm desde el navegador sin romper el diseño.
- [ ] El plugin es funcional sin agente: crear local, definir regla, editar plantilla, previsualizar e imprimir a mano.
- [ ] El plugin no contiene comprobación de licencia ni llamadas a servidores de Orvita.
- [ ] El `readme.txt` declara el servicio de pago y enlaza precios y términos.
- [ ] Cambiar una regla no altera trabajos ya encolados.

### Agente

- [ ] Se instala con `msiexec /qn` en un equipo limpio y queda operativo tras `orvitaprint pair`, sin pasos externos.
- [ ] Arranca tras reiniciar Windows sin sesión iniciada.
- [ ] Lista las impresoras instaladas y preselecciona la predeterminada.
- [ ] Permite añadir una o varias salidas, cada una con su impresora y su número de copias.
- [ ] Avisa si dos salidas apuntan a la misma impresora.
- [ ] La impresión de prueba sale en menos de 2 segundos.
- [ ] Si la impresora configurada no existe, el error es explícito en español.
- [ ] Si no hay impresoras instaladas, lo indica con ese texto.
- [ ] Exporta un ZIP de diagnóstico con `orvitaprint doctor --zip`.
- [ ] Un token revocado detiene el poller y `orvitaprint status` muestra el aviso.
- [ ] La actualización automática conserva la versión anterior si falla.

### Contrato

- [ ] Dos agentes simultáneos nunca imprimen el mismo trabajo.
- [ ] Un trabajo caducado no se imprime nunca.
- [ ] Un `ack` repetido es idempotente.
- [ ] El cursor avanza aunque un trabajo falle.
- [ ] Un documento con `version` desconocida se rechaza sin bloquear la cola.

---

## 4. Matriz de pruebas

### 4.1 Funcional

| Caso | Resultado esperado |
|---|---|
| Pedido nuevo con local válido | Un trabajo, estado `delivered` |
| Pedido sin regla coincidente | Trabajo al local por defecto + aviso |
| Dos pedidos simultáneos | Dos trabajos, dos tickets |
| Reimpresión manual | Trabajo nuevo, ticket nuevo |
| Pedido anulado antes de imprimir | Trabajo `cancelled`, sin papel |
| Plantilla con todos los campos activos | Ticket completo en 80 mm |
| Plantilla mínima | Ticket legible en 58 mm |
| `copies = 2` en una salida | Un trabajo, dos copias impresas |
| Dos salidas en dos impresoras | Dos trabajos, uno por impresora |
| Una impresora apagada y otra activa | Un trabajo `failed`, el otro `delivered` |
| Salida desactivada | No se crean trabajos nuevos; los pendientes se cancelan |
| Cambiar copias con trabajos pendientes | Los pendientes conservan sus copias |
| Plugin sin agente | Configura, previsualiza e imprime a mano; ninguna función del plugin queda bloqueada |
| Vista previa sin agente | Ticket legible en 80 mm, sin recursos externos |
| Impresión manual desde el navegador | Resultado fiel a la vista previa, sin depender del agente |

> Las plantillas distintas por rol quedan fuera de V1: todas las salidas imprimen el mismo documento.

### 4.2 Resiliencia

| Escenario | Comportamiento esperado |
|---|---|
| Servicio detenido 10 min | Los trabajos esperan; al arrancar se imprimen los vigentes |
| Impresora apagada 2 h | Los trabajos caducan; no hay avalancha al encender |
| Corte de luz durante la impresión | Al arrancar se reimprime y se marca `duplicate_suspected` |
| Pérdida de red 5 min | Backoff y reintento; sin pérdida de trabajos |
| Token revocado | Servicio detenido con aviso visible |
| Disco lleno | Error explícito en `orvitaprint status` y en los registros |
| Dos agentes en el mismo local | Uno gana el claim; el otro continúa sin duplicar |

### 4.3 Codificación

Obligatorio imprimir sin errores:

```
Pizza Margarita · jamón, piña, ñ, €, áéíóú
C/ Cañada 3, 2º B — 28801 Alcalá
```

Se prueba en CP850, CP858 y CP437. Si un carácter no existe en la tabla, el renderizador debe sustituirlo por uno seguro y registrar el aviso, **nunca** enviar bytes que rompan la impresora.

### 4.4 Impresoras

| Modelo | Conexión | Ancho | Estado |
|---|---|---|---|
| Epson TM-T20III | USB | 80 mm | Obligatorio |
| Xprinter XP-80C | USB | 80 mm | Obligatorio |
| Genérica ESC/POS | Ethernet 9100 | 80 mm | Obligatorio |
| Genérica 58 mm | USB | 58 mm | Deseable |

### 4.5 Sistema

| Elemento | Versiones a probar |
|---|---|
| Windows | 10 22H2 y 11 23H2 |
| WordPress | 6.8 y 7.1 |
| WooCommerce | 9.5 y 11.2 |
| PHP | 8.1 y 8.3 |
| HPOS | Activado y desactivado |

### 4.6 Seguridad

- [ ] Sin token → 401 en todos los endpoints protegidos.
- [ ] Token del local A no puede leer ni reclamar trabajos del local B.
- [ ] Código de emparejamiento caduca a los 10 minutos.
- [ ] Sexto intento fallido bloquea el código.
- [ ] Rate limit responde 429 y el agente hace backoff.
- [ ] El token no aparece en ningún registro.
- [ ] HTTP plano rechazado.

### 4.7 Carga

| Prueba | Criterio |
|---|---|
| 300 pedidos en 24 h simulados | Sin duplicados, sin pérdidas |
| 3 agentes consultando 24 h | < 1 % de errores |
| Trabajo de 8 KB × 1.000 | Panel responde en < 2 s |
| Retención de 90 días | Limpieza correcta |

---

## 5. Despliegue en la pizzería

| Etapa | Duración | Acción |
|---|---|---|
| 1. Preparación | 1 día | Instalar plugin, crear tres locales, reglas por ciudad, plantilla |
| 2. Local piloto | 1 semana | Un solo TPV con agente; email de respaldo activo |
| 3. Segundo y tercer local | 1 semana | Resto de TPV; email de respaldo activo |
| 4. Retirada del respaldo | 1 semana | Desactivar la notificación antigua tras 14 días limpios |
| 5. Estabilización | 30 días | Sin cambios; solo incidencias |

**Durante todo el piloto se mantiene el envío de email actual.** Es la red de seguridad y permite comparar: si el agente falla, el ticket sigue llegando por correo y el pedido no se pierde.

### Particularidades del checkout del piloto

El tema del cliente ya resuelve la selección de local; el plugin la lee sin tocar el checkout:

| Dato | Dónde está | Uso en el plugin |
|---|---|---|
| Local | `shipping_city` (radio de tres ciudades, lista cerrada) | Regla `city` → `location_id` |
| Subzona de reparto | `_shipping_zona` | Campo del ticket; **no** enruta |
| Dirección | `shipping_address_1` (incluye «Zona: X») | Campo del ticket |

- `billing_city` está desactivado en el checkout: el resolver debe leer **shipping primero** y no asumir billing.
- No hay código postal: las reglas por `postcode` no aplican en este cliente.
- El pedido nace en `on-hold` por la pasarela COD personalizada: el disparador correcto es **creación del pedido**, no cambio de estado.
- `_shipping_zona` se guarda hoy con `update_post_meta`; bajo HPOS puede no leerse con `$order->get_meta()`. Conviene pasarlo a CRUD antes del piloto.

### Métricas del piloto

| Métrica | Objetivo |
|---|---|
| Pedidos impresos / pedidos recibidos | 100 % |
| Duplicados | < 0,5 % |
| Tiempo medio pedido → papel | < 15 s |
| Incidencias | < 3 en 30 días |
| Intervenciones manuales | 0 |

---

## 6. Riesgos de ejecución

| Riesgo | Prob. | Impacto | Mitigación |
|---|---|---|---|
| Impresión desde sesión 0 falla con algún driver | Media | Alto | Probar en los tres TPV en F2; alternativa: tarea al inicio de sesión |
| SmartScreen bloquea el MSI | Media | Medio | Instalación por un profesional; instrucciones con capturas; firma solo si el volumen lo justifica |
| Codificación defectuosa | Media | Alto | Prueba obligatoria antes de cada release |
| El driver no acepta RAW | Media | Medio | Fallback GDI implementado y probado |
| Alcance crece durante el desarrollo | Alta | Alto | Congelar el contrato en F0; lo nuevo va al backlog |
| El socio no puede dedicar las horas | Media | Alto | Plan de 10 semanas asume jornada completa |

---

## 7. Checklist de release

- [ ] Versiones de plugin y agente alineadas con el contrato.
- [ ] `readme.txt` y `CHANGELOG.md` actualizados.
- [ ] Probado en las cuatro impresoras de la matriz.
- [ ] Probado en Windows 10 y 11.
- [ ] Documento neutro validado contra el esquema.
- [ ] SHA-256 del MSI publicado y verificado tras la descarga.
- [ ] Manifiesto de actualización firmado.
- [ ] Copia de seguridad de la base de datos antes de migrar.
- [ ] Plan de vuelta atrás documentado.
- [ ] Registro de cambios enviado a los clientes con licencia activa.

---

## 8. Backlog posterior a V1

| Prioridad | Elemento | Valor |
|---|---|---|
| Alta | Integración con SIF homologado (Verifactu / TicketBAI) | Imprime el ticket fiscal generado por un tercero; no exige certificar el producto propio |
| Alta | Conector PrestaShop (8.x y 9.x) | Segunda plataforma más barata: mismo modo directo, sin relay |
| Media | Relay para Shopify | Mayor mercado, pero exige infraestructura propia y RGPD |
| Media | Plantillas distintas por rol | Cocina sin precios, resguardo de cliente, etiqueta de reparto |
| Media | Envío del ticket al cliente por email/WhatsApp | Reduce llamadas del cliente final |
| Media | Versión Android | Elimina el coste de soporte de Windows |
| Media | GUI de gestión del agente (estado tipo bandeja) | **Planificada en `08-gui-agente-avalonia.md`** (tareas GUI-01..08, ~5,5 días). Otra cliente de la pipe de gestión (ADR-13), sin capacidades nuevas en el servicio. Gatillo: piloto F4 o demanda del canal de venta |
| Baja | Panel de métricas de impresión | Argumento de venta para agencias |
| Baja | White-label completo | Necesario para el SKU Ilimitado |
