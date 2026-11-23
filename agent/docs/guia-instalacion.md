# Guía de instalación del agente Orvita Print

Guía para el **profesional instalador** (Doc/01: el producto se vende al técnico). Al final de
esta guía tendrás un TPV con Windows imprimiendo los pedidos de WooCommerce en una impresora
térmica, con el servicio arrancando solo tras cada reinicio.

> **Todo lo que instalas** es el agente: un servicio de Windows headless sin interfaz gráfica.
> Se configura y se diagnostica todo por el comando `orvitaprint` (linea de comandos) — hay
> copia de cada comando en esta guía y en `orvitaprint help`.

---

## 1. Prerrequisitos

| Requisito | Detalle |
|---|---|
| Sistema operativo | Windows 10 22H2 o Windows 11, **x64** (no funciona en 32 bits ni ARM) |
| Hardware | TPV en la misma red que el servidor WordPress (salida a Internet) |
| Impresora | Térmica 80/58 mm instalada en Windows con su driver del fabricante (Epson TM-T20III, Epson TM-m30, Xprinter XP-58/80 en la matriz de pruebas) |
| Permisos | Administrador local para instalar el MSI y configurar impresoras |
| Datos | URL de emparejamiento del servidor y **código de 6 dígitos** que genera el panel de WordPress (Locales → «Generar código»; caduca en 10 minutos) |

**No necesitas** instalar .NET: el agente va self-contained (todo incluido en el MSI).

---

## 2. Descarga y verificación del SHA-256

Descarga el instalador **solo desde la web oficial de Orvita** (te la facilita el comercial junto
a la licencia). Junto al enlace verás el **SHA-256** del MSI: verifícalo ANTES de instalar.

```bat
certutil -hashfile C:\Users\TU-USUARIO\Downloads\OrvitaPrint-1.0.0.msi SHA256
```

La primera línea debe ser **idéntica** al SHA-256 publicado (mismos 64 caracteres
hexadecimales). Si no coincide, NO instales: pide el fichero de nuevo.

[CAPTURA PENDIENTE: certutil junto al SHA-256 publicado en la web]

---

## 3. Instalación del MSI

### 3.1 Instalación con doble clic (con aviso de SmartScreen)

El MSI **no está firmado digitalmente con certificado de editor** en V1 (así lo documenta
Orvita; el control de integridad es el SHA-256 del paso anterior). Por eso Windows muestra el
aviso de SmartScreen la primera vez:

1. Doble clic en `OrvitaPrint-1.0.0.msi`.
2. Si aparece «Windows protegió tu equipo»:
   - Texto: *«Windows protegió tu equipo — Microsoft Defender SmartScreen impidió el inicio de
     una aplicación no reconocida… Editor: desconocido»*.
   - Pulsa **«Más información»** (aparece el botón **«Ejecutar de todas formas»**) y púlsalo.
3. Sigue el asistente (siguiente → siguiente → instalar). Requiere aceptar el aviso de UAC
   («¿Quieres permitir que esta aplicación haga cambios…?») → **Sí**.

[CAPTURA PENDIENTE: SmartScreen con «Más información»]
[CAPTURA PENDIENTE: botón «Ejecutar de todas formas»]
[CAPTURA PENDIENTE: asistente del MSI]

### 3.2 Instalación silenciosa (scripts / RMM)

Como administrador:

```bat
msiexec /i OrvitaPrint-1.0.0.msi /qn /norestart
```

Con `* /norestart*` el equipo no se reinicia; el servicio arranca inmediatamente. Sin `/qn`
(verás el asistente). Útil también `/l*v install.log` para dejar log de la instalación.

**Opcional — directorio de datos alternativo** (por defecto todo va en
`%ProgramData%\OrvitaPrint`): añade la property pública `ORVDATA`:

```bat
msiexec /i OrvitaPrint-1.0.0.msi /qn ORVDATA="D:\OrvitaDatos"
```

### 3.3 Qué instala

| Ubicación | Contenido |
|---|---|
| `C:\Program Files\Orvita Print\service\` | Binarios del servicio (self-contained) |
| `C:\Program Files\Orvita Print\cli\` | `orvitaprint.exe` (añadido al PATH de la máquina) |
| `%ProgramData%\OrvitaPrint\` | Datos: base de datos local (`outbox.db`), token cifrado (DPAPI), `license.dat`, logs (7 días) y diagnósticos |
| Registro de servicios | **OrvitaPrint**, arranque **automático**, ejecutándose como LocalSystem |

Tras la instalación, el servicio ya está en marcha (lo verifica el paso 4). **Abre una consola
NUEVA** para que el PATH del CLI esté disponible, y comprueba:

```bat
sc query OrvitaPrint        →  STATE: 4 RUNNING
orvitaprint status          →  «Agente Orvita Print 1.x (windows) … Emparejado: NO …»
```

[CAPTURA PENDIENTE: sc query en RUNNING]
[CAPTURA PENDIENTE: orvitaprint status sin emparejar]

---

## 4. Colocar la licencia (si el cliente la compró)

La licencia (`license.dat`, entregada por Orvita junto a la compra) se copia al directorio de
datos y se reinicia el servicio:

```bat
copy license.dat "%ProgramData%\OrvitaPrint\license.dat"
sc stop OrvitaPrint && sc start OrvitaPrint
orvitaprint status
```

En `status` debe aparecer: **«Licencia: 10 — 10 local(es) · actualizaciones hasta 2027-11-23 ·
Pizzería Río Rosso»** (los datos del cliente).

Notas:

- La validación es **local** (clave pública embebida en el agente, sin conexión).
- **Sin licencia el agente funciona igualmente** limitado a 1 local: útil para el piloto y para
  pruebas; el estado lo indica claramente en `status`.
- Una licencia con `updates_until` pasado **no caduca el agente**: sigue imprimiendo y solo deja
  de aceptar actualizaciones automáticas.

[CAPTURA PENDIENTE: status con licencia activa]

---

## 5. Emparejar el agente con la tienda

Antes: en el panel de WordPress (**Orvita Print → Locales y reglas → botón «Generar código»**)
crea un código de 6 dígitos (caduca a los 10 minutos, 5 intentos).

```bat
orvitaprint pair --server https://TU-TIENDA.es/wp-json/orp-print/v1 482913
```

- La URL te la da el panel en la misma pantalla del código (cópiala tal cual: debe ser
  `https://` — el agente **rechaza** conexiones sin TLS).
- Éxito: «Emparejado correctamente» y el agente queda conectado EN CALIENTE (sin reiniciar nada).
- Errores típicos: `400 orp_pairing_invalid` (código mal escrito), `410 orp_pairing_expired`
  (caducado → genera otro), `429 orp_pairing_locked` (bloqueado 15 min por 5 fallos → espera).

El **token** resultante se guarda cifrado con DPAPI en la base de datos local: nunca está en
claro en disco, en logs ni en los diagnósticos.

[CAPTURA PENDIENTE: pair correcto]
[CAPTURA PENDIENTE: panel con el código de 6 dígitos]

---

## 6. Configurar las salidas (impresoras)

Lista las impresoras instaladas con su nombre EXACTO (ese nombre es el que se guarda):

```bat
orvitaprint destination list
```

Añade una salida por punto de impresión (rol único, copias 1–99):

```bat
orvitaprint destination add --printer "EPSON TM-T20III" --copies 2 --label "Mostrador"
orvitaprint destination add --role cocina --printer "Xprinter XP-N160II" --label "Cocina"
```

Variantes: `--enabled false` para dejar una salida configurada pero inactiva; salidas de red sin
driver con `--kind tcp9100 --address 192.168.1.50` (o `host:puerto`). Avisos frecuentes (no
bloquean): impresora no encontrada (revisa el nombre exacto con `destination list`), dos roles a
la misma impresora (para dos copias usa `--copies`, no dos salidas).

El panel de WordPress muestra las salidas en cuanto el siguiente latido del agente las reporta
(máx. 60 s).

[CAPTURA PENDIENTE: destination list con las impresoras del TPV]

---

## 7. Probar y verificar

```bat
orvitaprint test                :: ticket de prueba por la salida por defecto
orvitaprint test --role cocina  :: por una salida concreta
orvitaprint status              :: emparejamiento, cola, licencia, último error
orvitaprint doctor              :: resumen técnico completo
```

En el ticket de prueba verifica: el nombre del negocio en doble ancho, la línea
«Pizza Margarita · jamón, piña, ñ, €, áéíóú» **sin caracteres rotos**, el corte al final y las
copias configuradas. Si algo falla, `status` muestra el último error de impresión con su código
(`orp_printer_not_found`, `orp_print_failed`, …).

[CAPTURA PENDIENTE: ticket de prueba real en Epson y Xprinter]
[CAPTURA PENDIENTE: orvitaprint status emparejado y con cola en verde]

---

## 8. Diagnóstico para soporte (`doctor --zip`)

Si algo va raro, genera el paquete de diagnóstico y envíalo a soporte:

```bat
orvitaprint doctor --zip
orvitaprint doctor --zip --out C:\Temp\diagnostico.zip   :: copia donde quieras adjuntar
```

El ZIP contiene los logs de los últimos 7 días y un informe técnico (versiones,
emparejamiento, cola, impresoras, licencia). **No contiene el token** ni datos de clientes:
puedes enviarlo con confianza.

---

## 9. Arranque automático y reinicios

El MSI deja el servicio **OrvitaPrint** con arranque automático: tras un corte de luz o reinicio
del TPV el agente se levanta solo, recarga el emparejamiento del almacén local (no hace falta
re-emparejar), re-imprime cualquier ticket que quedara a medias (marcado como
«duplicado sospechoso») y retoma el poller. No necesita sesión de usuario abierta.

Comandos útiles (como administrador):

```bat
sc query OrvitaPrint          :: estado
sc stop OrvitaPrint           :: parar
sc start OrvitaPrint          :: arrancar
orvitaprint restart-poller    :: reiniciar el bucle de descarga sin tirar el servicio
```

---

## 10. Actualizaciones automáticas

Con el canal configurado (lo activa Orvita en tu instalación), el agente comprueba **una vez al
día** si hay versión nueva. Cuando la hay:

1. Descarga el nuevo MSI y verifica su **firma Ed25519** (manifiesto) y su **SHA-256**.
2. Ejecuta la actualización en silencio: el instalador para el servicio, reemplaza los binarios
   y lo vuelve a arrancar (unos segundos sin imprimir).
3. Si CUALQUIER paso falla (firma, hash, red, msiexec), **se conserva la versión actual** y se
   reintenta al día siguiente: el estado queda visible en `orvitaprint doctor`.

En `orvitaprint doctor` verás la sección de actualizaciones (último chequeo y resultado). Las
actualizaciones se detienen cuando la licencia pasa su fecha `updates_until` (el agente sigue
funcionando); renovar la licencia y reiniciar el servicio las reactiva.

---

## 11. Desinstalación

```bat
msiexec /x OrvitaPrint-1.0.0.msi /qn
```

(o Añadir/Quitar programas → Orvita Print → Desinstalar; pide UAC)

**Qué se elimina:** los binarios de `C:\Program Files\Orvita Print\` y el registro del servicio
(el MSI lo para y elimina solo).

**Qué SE CONSERVA:** `%ProgramData%\OrvitaPrint\` (base de datos, licencia, logs). Reinstalar no
pierde el emparejamiento ni la licencia: instala el MSI nuevo y el servicio vuelve a funcionar
con todo lo anterior. Si quieres una limpieza TOTAL, borra esa carpeta a mano después de
desinstalar (pierde el emparejamiento: haría falta un `pair` nuevo).

[CAPTURA PENDIENTE: desinstalación desde Añadir/Quitar programas]

---

## 12. Problemas frecuentes

| Síntoma | Qué mirar |
|---|---|
| `orvitaprint` no se reconoce | Abre una consola NUEVA (el PATH lo añade el MSI) o usa la ruta completa `C:\Program Files\Orvita Print\cli\orvitaprint.exe` |
| «El servicio Orvita Print no está en ejecución» (código 3) | `sc query OrvitaPrint`; arráncalo con `sc start OrvitaPrint` |
| `pair` con 429 | Código bloqueado 15 min por 5 intentos fallidos; genera otro código y espera |
| La impresora no aparece en `destination list` | Driver no instalado o impresora compartida de otro equipo; instálala localmente en el TPV |
| El ticket sale con caracteres rotos | Es un problema de la tabla de código: avisa a soporte (el agente imprime con CP850/CP858/CP437 configurables desde el panel) |
| SmartScreen bloquea el MSI | Es normal sin firma de código (verificación SHA-256 del paso 2); «Más información» → «Ejecutar de todas formas» |
| El test imprime pero no llegan pedidos | Revisa en WordPress: salida activa, agente «conectado» en la cola, y reglas del local (que el pedido caiga en un local con salidas) |
