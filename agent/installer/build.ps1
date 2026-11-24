#Requires -Version 5.1
<#
.SYNOPSIS
    Compila el MSI de Orvita Print (F3-01, Doc/03 §9): publica los binarios self-contained
    win-x64, cosecha los ficheros en un fragmento WiX, compila con wix build y deja el SHA-256.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1
    powershell -ExecutionPolicy Bypass -File build.ps1 -Configuration Release -SkipPublish

.NOTES
    - Requiere `dotnet tool install --global wix` (tooling de build, NO se distribuye).
    - NO instala el MSI en esta máquina: para validar usa build.ps1 -Validate
      (extracción administrativa `msiexec /a` a un temporal, no requiere admin).
    - El MSI resultante NO está firmado con Authenticode (decisión V1, Doc/03 §9): se publica
      su SHA-256 en artifacts\checksum.txt y la guía documenta el aviso de SmartScreen.
#>
param(
    [string]$Configuration = "Release",

    [switch]$SkipPublish,

    [switch]$Validate
)

$ErrorActionPreference = "Stop"
$installerDir = $PSScriptRoot
$agentDir = Split-Path $installerDir -Parent
$publishRoot = Join-Path $installerDir "publish"
$artifactsDir = Join-Path $installerDir "artifacts"
$servicePublish = Join-Path $publishRoot "service"
$cliPublish = Join-Path $publishRoot "cli"
$guiPublish = Join-Path $publishRoot "gui"
$wixExe = Join-Path $env:USERPROFILE ".dotnet\tools\wix.exe"

if (-not (Test-Path $wixExe))
{
    throw "No se encuentra wix.exe en $wixExe. Instala la herramienta de build: dotnet tool install --global wix"
}

# --------------------------------------------------------------------------- 1. Versión
# ÚNICA fuente de la versión: Directory.Build.props (1.0.0-dev → MSI 1.0.0; ProductVersion
# solo admite números, el sufijo -dev es de desarrollo y se recorta).
[xml]$buildProps = Get-Content (Join-Path $agentDir "Directory.Build.props") -Raw -Encoding UTF8
$versionFull = $buildProps.Project.PropertyGroup.Version
$productVersion = ($versionFull -split '-')[0]
if ($productVersion -notmatch '^\d+(\.\d+){1,3}$')
{
    throw "Versión no válida para ProductVersion: «$versionFull»"
}

Write-Host "== Orvita Print MSI $productVersion (Configuration $Configuration, self-contained win-x64) =="

# ------------------------------------------------------------------------- 2. Publicación
if (-not $SkipPublish)
{
    Write-Host "-- dotnet publish (service)…"
    dotnet publish (Join-Path $agentDir "src\Orvita.Print.Service") `
        -c $Configuration -r win-x64 --self-contained true `
        -o $servicePublish --nologo /v:m
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish del servicio falló." }

    Write-Host "-- dotnet publish (cli)…"
    dotnet publish (Join-Path $agentDir "src\Orvita.Print.Cli") `
        -c $Configuration -r win-x64 --self-contained true `
        -o $cliPublish --nologo /v:m
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish del CLI falló." }

    # GUI de bandeja (GUI-07, Doc/08 §5): mismo exe self-contained, junto al servicio y el
    # CLI. Sin recortar (misma decisión documentada que el servicio: Serilog/SQLite/Avalonia
    # no permiten trim sin riesgo; el tamaño se mide y se reporta abajo).
    Write-Host "-- dotnet publish (gui)…"
    dotnet publish (Join-Path $agentDir "src\Orvita.Print.Gui") `
        -c $Configuration -r win-x64 --self-contained true `
        -o $guiPublish --nologo /v:m
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish de la GUI falló." }
}

foreach ($dir in @($servicePublish, $cliPublish, $guiPublish))
{
    if (-not (Test-Path $dir)) { throw "Falta la salida de publish: $dir (ejecuta sin -SkipPublish)." }
}

# Tamaño documentado (Doc/07 F3-01 + Doc/08 §9): self-contained SIN recortar; se mide antes
# de decidir un trim (riesgo > beneficio en V1).
$serviceMb = [math]::Round((Get-ChildItem $servicePublish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$cliMb = [math]::Round((Get-ChildItem $cliPublish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
$guiMb = [math]::Round((Get-ChildItem $guiPublish -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host ("   publicados: service {0} MB · cli {1} MB · gui {2} MB" -f $serviceMb, $cliMb, $guiMb)

# --------------------------------------------------------------------------- 3. Cosecha
# Genera PublishedFiles.wxs: un componente por fichero (KeyPath en el fichero, GUID autogenerado
# por WiX a partir de la ruta destino). EXCLUYE *.pdb y el exe del servicio (va en Product.wxs
# con el registro del servicio).
function Harvest([string]$Dir, [string]$DirectoryId, [string]$ComponentGroupId, [string[]]$ExcludeNames)
{
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $components = foreach ($file in (Get-ChildItem $Dir -File | Sort-Object Name))
    {
        if ($ExcludeNames -contains $file.Name) { continue }

        # Ids únicos en TODA la MSI y ≤ 72 caracteres: prefijo del grupo + nombre saneado
        # recortado + huella corta del nombre completo (evita colisiones del recorte).
        $sanitized = ($file.Name -replace '[^A-Za-z0-9_.]', '_') -replace '\.', '_'
        if ($sanitized.Length -gt 30) { $sanitized = $sanitized.Substring(0, 30) }
        $hashBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($file.Name))
        $fingerprint = -join ($hashBytes[0..4] | ForEach-Object { $_.ToString("x2") })

@"
        <Component Id="$ComponentGroupId.$sanitized.$fingerprint" Directory="$DirectoryId">
          <File Id="$ComponentGroupId.$sanitized.$fingerprint.File" Name="$($file.Name)" Source="$Dir\$($file.Name)" KeyPath="yes" />
        </Component>
"@
    }

@"
    <Fragment>
      <ComponentGroup Id="$ComponentGroupId">
$(($components -join "`n"))
      </ComponentGroup>
    </Fragment>
"@
    $sha256.Dispose()
}

$harvestedFragments = @(
    (Harvest $servicePublish "ServiceFolder" "ServiceFiles" @("Orvita.Print.Service.exe", "Orvita.Print.Service.pdb")),
    (Harvest $cliPublish "CliFolder" "CliFiles" @("orvitaprint.pdb")),
    (Harvest $guiPublish "GuiFolder" "GuiFiles" @("Orvita.Print.Gui.pdb"))
)
# Un ÚNICO elemento raíz por fichero .wxs.
@"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
$($harvestedFragments -join "`n")
</Wix>
"@ | Out-File (Join-Path $installerDir "PublishedFiles.wxs") -Encoding utf8
Write-Host "-- cosecha: PublishedFiles.wxs generado"

# --------------------------------------------------------------------------- 4. wix build
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
$msiPath = Join-Path $artifactsDir "OrvitaPrint-$productVersion.msi"

Write-Host "-- wix build…"
& $wixExe build `
    (Join-Path $installerDir "Product.wxs") (Join-Path $installerDir "PublishedFiles.wxs") `
    -arch x64 `
    -d "ProductVersion=$productVersion" `
    -d "ServicePublish=$servicePublish" `
    -d "CliPublish=$cliPublish" `
    -d "GuiPublish=$guiPublish" `
    -o $msiPath
if ($LASTEXITCODE -ne 0) { throw "wix build falló." }

# --------------------------------------------------------------------------- 5. SHA-256
# Formato sha256sum (misma línea que actualizará orvita-release genmanifest --checksum-out).
$hash = (Get-FileHash $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $artifactsDir "checksum.txt"
$fileName = Split-Path $msiPath -Leaf
$lines = @(Get-Content $checksumPath -ErrorAction SilentlyContinue) |
    Where-Object { $_ -and -not $_.EndsWith("  $fileName", [System.StringComparison]::OrdinalIgnoreCase) }
$lines += "$hash  $fileName"
$lines | Out-File $checksumPath -Encoding ascii

Write-Host ""
Write-Host "== MSI generado =="
Write-Host "   $msiPath ($([math]::Round((Get-Item $msiPath).Length / 1MB, 1)) MB)"
Write-Host "   SHA-256: $hash"
Write-Host "   Checksum: $checksumPath"

# --------------------------------------------------------------------------- 6. Validación
if ($Validate)
{
    # Extracción administrativa (NO requiere admin NI instala nada): msiexec /a vuelca el árbol.
    $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("orvita-msi-validate-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    Write-Host "-- validación msiexec /a → $extractDir"
    Start-Process msiexec.exe -ArgumentList "/a", "`"$msiPath`"", "/qn", "TARGETDIR=`"$extractDir`"" -Wait
    $extracted = Get-ChildItem $extractDir -Recurse -File
    foreach ($required in @("Orvita.Print.Service.exe", "orvitaprint.exe", "Orvita.Print.Gui.exe", "hostfxr.dll", "appsettings.json"))
    {
        if (-not ($extracted | Where-Object Name -eq $required))
        {
            throw "Validación: falta «$required» en la extracción administrativa."
        }
    }

    Write-Host ("   OK: {0} ficheros extraídos, binarios y componentes presentes." -f $extracted.Count)
    Write-Host "   (directorio temporal sin borrar para inspección: $extractDir)"
}
