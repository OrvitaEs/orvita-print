# orvita-release — herramienta de release de Orvita Print (F3-02/F3-03)

Herramienta **INTERNA** de Orvita: genera los pares de claves de firma, las licencias de los
clientes y el manifiesto de actualización firmado. **NO se distribuye en el MSI ni llega al
cliente** — vive solo en este repositorio (`agent/tools/`, en la solución para que compile con
`dotnet build`).

```powershell
# desde agent/tools/Orvita.Print.ReleaseTools (o con la ruta de la dll):
dotnet run --project . -- <subcomando> …
# o, compilado:
dotnet build -c Release
dotnet bin\Release\net10.0\orvita-release.dll --help
```

## Claves: dónde viven (NUNCA en el repositorio)

| Uso | Algoritmo | Privada | Pública |
|---|---|---|---|
| **Licencias** (`license.dat`) | ECDSA P-256 (SHA-256) | `orp-license-private.pem` (PKCS#8) | EMBEBIDA en el agente: `Orvita.Print.Core/LicenseTrust.ReleasePublicKeyBase64` |
| **Manifiestos** (`latest.json`) | Ed25519 | `orp-manifest-private-seed.key` (base64 de la semilla de 32 bytes) | EMBEBIDA en el agente: `Orvita.Print.Core/UpdateTrust.ReleasePublicKeyBase64` |

Reglas de custodia:

1. Las claves privadas se generan en un directorio **FUERA del repositorio** con copia de
   respaldo OFFLINE (usb cifrado / gestor de contraseñas). `agent/.gitignore` las bloquea por
   si acaso (`*orp-license-private.pem`, `*orp-manifest-private-seed.key`, `orvita-release-keys/`).
2. **Nunca en el servidor** que publica el MSI/latest.json: la firma se hace en la máquina del
   desarrollador (Doc/03 §9: el que firma no es el que publica).
3. Si se pierde una privada: las licencias ya emitidas siguen validando (la pública embebida no
   cambia), pero no se pueden emitir nuevas hasta rotar la clave embebida y publicar una nueva
   versión del agente. Para updates: sin semilla no se puede publicar ningún manifiesto válido.
4. `genkey` se niega a sobrescribir claves existentes sin `--force` (rotación explícita).

Ubicación recomendada (y usada en el release actual): `%USERPROFILE%\OneDrive\Documentos\orvita-release-keys\`
(backup automático de OneDrive + fuera del repo). Cualquier ubicación equivalente vale; lo
importante es el punto 1.

## Convención de firma (la que verifica el agente)

La MISMA disciplina para licencia y manifiesto (documentada en `LicenseDocument` y
`UpdateManifest` del Core):

- La firma cubre SOLO los campos firmados, en ESE orden, serializados canónicamente: JSON
  compacto UTF-8 sin BOM, escapado mínimo, `max_locations` como número.
- `signature` va al final del objeto; los campos extra y el whitespace/orden del fichero
  publicado quedan FUERA de la firma (el verificador re-canonicaliza los valores PARSEADOS).
- Licencia: ECDSA P-256/SHA-256, firma r||s (IEEE P1363, 64 bytes) en base64.
  Manifiesto: Ed25519, 64 bytes en base64.

## Flujo de release completo (Doc/05 §7)

```powershell
# 0. (una vez) instalar WiX, tooling de build:
dotnet tool install --global wix
wix eula accept wix7          # OSMF: gratuito para organizaciones < 10k USD/año

# 1. Subir la versión en agent/Directory.Build.props (p. ej. 1.0.0-dev → 1.1.0-dev).

# 2. Compilar el MSI (publica self-contained win-x64, cosecha, valida con ICE):
powershell agent\installer\build.ps1
#    → agent/installer/artifacts/OrvitaPrint-<v>.msi + artifacts/checksum.txt (SHA-256)

# 3. Probar el MSI en máquina limpia (manual, NO en la de desarrollo):
#    instalación /qn con admin, pair + destination + test (ver agent/docs/guia-instalacion.md).

# 4. Firmar el manifiesto de actualización (la clave privada NUNCA sale de aquí):
cd agent
dotnet tools\Orvita.Print.ReleaseTools\bin\Release\net10.0\orvita-release.dll genmanifest `
    --key "$env:USERPROFILE\OneDrive\Documentos\orvita-release-keys\orp-manifest-private-seed.key" `
    --msi installer\artifacts\OrvitaPrint-1.1.0.msi `
    --url https://descargas.orvita.es/agent/OrvitaPrint-1.1.0.msi `
    --version 1.1.0 --min-supported 1.0.0 `
    --out installer\artifacts\latest.json `
    --checksum-out installer\artifacts\checksum.txt

# 5. Subir los TRES ficheros juntos a la ubicación estática del canal de updates
#    (Orvita:Update:ManifestUrl en appsettings.json del agente):
#      - OrvitaPrint-1.1.0.msi   (el binario)
#      - latest.json             (manifiesto firmado: versión + url + sha256 + firma)
#      - checksum.txt            (SHA-256 publicado junto a la descarga, Doc/03 §9)
#    El agente verifica firma Ed25519 del manifiesto + SHA-256 del MSI antes de msiexec /qn.

# 6. Licencias de clientes (con la compra):
orvita-release genlicense `
    --key "$env:USERPROFILE\OneDrive\Documentos\orvita-release-keys\orp-license-private.pem" `
    --sku 10 --customer "Pizzería Río Rosso" --updates-until 2027-11-23 `
    --out .\license.dat
#    → el cliente copia license.dat a %ProgramData%\OrvitaPrint\license.dat y reinicia el
#      servicio; la validación es LOCAL (clave pública embebida, sin conexión, Doc/03 §10).
```

## Salidas de ejemplo (release 1.0.0)

### genlicense (SKU 10 locales)

```
license.dat generado:
  Fichero:         C:\…\license.dat
  license_id:      orp_3f2a9c1e8b7d4a5f9e0c2b1d8a7c6e5f
  sku:             10 (max_locations = 10)
  customer:        Pizzería Río Rosso
  updates_until:   2027-11-23 (actualizaciones automáticas hasta ese día INCLUIDO)
```

```json
{"license_id":"orp_3f2a9c1e8b7d4a5f9e0c2b1d8a7c6e5f","sku":"10","max_locations":10,"updates_until":"2027-11-23","customer":"Pizzería Río Rosso","signature":"MEUCIQ…"}
```

### genmanifest (canal de updates)

```
latest.json firmado (Ed25519, convención canónica de UpdateManifest):
  Manifiesto:    …\installer\artifacts\latest.json
  MSI:           …\installer\artifacts\OrvitaPrint-1.0.0.msi
  sha256:        584eead8cacc62b59d4e50aa7ad810a13698fb44562ffe3dfec21427791f5ad0
  versión:       1.0.0 (min_supported 1.0.0)
  url:           https://descargas.orvita.es/agent/OrvitaPrint-1.0.0.msi
  Checksums:     …\installer\artifacts\checksum.txt (línea actualizada)
```

```json
{"version":"1.0.0","url":"https://descargas.orvita.es/agent/OrvitaPrint-1.0.0.msi","sha256":"584eead8cacc62b59d4e50aa7ad810a13698fb44562ffe3dfec21427791f5ad0","min_supported":"1.0.0","signature":"q8…"}
```

### checksum.txt (formato sha256sum)

```
584eead8cacc62b59d4e50aa7ad810a13698fb44562ffe3dfec21427791f5ad0  OrvitaPrint-1.0.0.msi
```

## Políticas de SKU (F3-02, Doc/00 "Modelo de negocio")

| `--sku` | max_locations | Precio |
|---|---|---|
| `1` | 1 | 79 € (1 local) |
| `10` | 10 | 279 € (10 locales) |
| `unlimited` | 0 (ilimitado) | 549 € (ilimitado + white-label del plugin) |

`updates-until` = 12 meses de actualizaciones desde la compra (pago único). Pasada esa fecha la
licencia SIGUE siendo válida (el agente funciona) y solo deja de aceptar actualizaciones
automáticas (Doc/03 §10).
