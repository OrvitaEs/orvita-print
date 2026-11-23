# Checklist de release — Orvita Print

> Adaptación de `Doc/05` §7 a las piezas del proyecto. Las filas marcadas
> **[plugin]** corresponden a este repositorio; las **[agente]** y **[común]**
> se ejecutan con el instalador/paquete comercial (fases F3–F5).

## 1. Versiones alineadas [común]

- [ ] Versión del **plugin** (`orp-print.php` → `Version:` y `ORP_VERSION`) igual a la del **agente** y a la del **contrato** (`Router::API_VERSION`, documento neutro `version: 1`).
- [ ] `Stable tag` de `readme.txt` igual a la versión del plugin.
- [ ] `Requires at least` (WP), `Requires PHP` y compatibilidad HPOS declarada (`before_woocommerce_init`).
- [ ] `orp_db_version` correcto: si el esquema cambió, migración nueva (nunca editar una publicada).
- [ ] `min_supported` del manifiesto de actualización del agente coherente con la versión mínima soportada.

## 2. readme, changelog y precios [plugin]

- [ ] `readme.txt` actualizado (formato WordPress.org: Description, Installation, FAQ, Screenshots, Changelog).
- [ ] El readme **declara el servicio de pago** (agente de Windows, pago único) y **enlaza precios y términos** (Guideline 5; criterio `05` §3).
- [ ] El dominio comercial está decidido: sustituir `orvitaprint.example` (Plugin URI y enlaces de precios/términos) por el definitivo. *(Decisión pendiente del socio, Doc/00.)*
- [ ] `CHANGELOG.md` del plugin al día con la versión publicada.
- [ ] `.pot` regenerado (`wp i18n make-pot . languages/orp-print.pot --domain=orp-print`) y auditoría i18n sin problemas (`tools/_audit_i18n.php`).

## 3. Pruebas [plugin] [agente]

- [ ] Suite PHPUnit del plugin en verde (`npx @wordpress/env run tests-cli bash tools/_t_phpunit.sh`).
- [ ] Suite xUnit del agente en verde (`dotnet test` en `agent/`).
- [ ] Auditoría de seguridad de `05` §4.6 verificada en esta versión (`SECURITY-AUDIT.md`): 401/404/410/429, aislamiento por local, token fuera de los registros, sexto intento, Retry-After.
- [ ] Humo HTTP del contrato (`tools/_verify_f1_10_12_13.sh` y `tools/_verify_f1_07_08_09.sh`) en verde.

## 4. Matriz de impresión [agente]

- [ ] Probado en las impresoras de la matriz (`05` §4.4): cadena «Pizza Margarita · jamón, piña, ñ, €, áéíóú» correcta en CP850/CP858/CP437.
- [ ] Probado en Windows 10 y 11 (sesión 0 incluida).

## 5. Documento y contrato [común]

- [ ] Documento neutro validado contra el esquema de `Doc/04` §7.2 (los 12 bloques; bloques desconocidos tolerados por el agente, `05` §3 Contrato).
- [ ] Contrato REST `orp-print/v1` sin cambios, o cambiado con versión nueva y acuerdo previo (el contrato está congelado).

## 6. Paquete e instalador [agente]

- [ ] MSI compilado y **SHA-256 publicado**; verificado tras descargarlo.
- [ ] Manifiesto de actualización (`latest.json`) **firmado** (Ed25519 con la clave de release, no la de desarrollo).
- [ ] Instalación en máquina limpia con `msiexec /qn` + `orvitaprint pair` operativa, sin pasos externos.

## 7. Copia de seguridad y vuelta atrás [común]

- [ ] Copia de seguridad de la base de datos antes de migrar (`wp db export` en el sitio del cliente).
- [ ] Plan de vuelta atrás documentado: plugin anterior (SVN/zip), MSI anterior conservando la versión, y `orp_db_version` sin migraciones destructivas.
- [ ] Registro de cambios enviado a los clientes con licencia activa (soporte, `05` §7).

## 8. Criterios de no-regresión del plugin (checklist rápido)

- [ ] Un pedido nuevo genera exactamente un trabajo (checkout clásico y de bloques).
- [ ] La reimpresión manual genera trabajo nuevo; la vista previa NO crea ninguno.
- [ ] Sin agente: locales, reglas, plantilla, vista previa e impresión a mano funcionan.
- [ ] Sin comprobación de licencia ni llamadas a servidores de Orvita (grep limpio).
- [ ] Página de Estado sin avisos de PHP y con las comprobaciones automáticas en verde.
