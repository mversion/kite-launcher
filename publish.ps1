<#
.SYNOPSIS
  Builds a Kite release into .\publish:
    Kite\                       the app (Kite.exe + skins)
    KiteSetup-<version>.exe     installer (choose "Install on this PC" or "Portable")
    Kite-<version>-portable.zip unzip anywhere and run (keeps settings next to Kite.exe)

.PARAMETER SelfContained
  Bundle the .NET runtime into Kite.exe so it runs on PCs without .NET 8 (much larger).
#>
param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'publish'
$app = Join-Path $publish 'Kite'
$payloadDir = Join-Path $publish 'payload'
$payload = Join-Path $payloadDir 'Kite.zip'

dotnet test (Join-Path $root 'tests\Kite.Tests') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }

# 1. The app
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish (Join-Path $root 'src\Kite.App') -c Release -o $app --nologo -p:SelfContained=$sc
if ($LASTEXITCODE -ne 0) { throw 'Publishing Kite failed.' }
$version = (Get-Item (Join-Path $app 'Kite.exe')).VersionInfo.ProductVersion -replace '\+.*$', ''

# 2. Payload for the installer
New-Item -ItemType Directory -Force $payloadDir | Out-Null
if (Test-Path $payload) { Remove-Item $payload -Force }
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $payload -CompressionLevel Optimal

# 3. Installer (embeds the payload)
$setupOut = Join-Path $publish 'setup-build'
if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
dotnet publish (Join-Path $root 'src\Kite.Setup') -c Release -o $setupOut --nologo "-p:PayloadZip=$payload"
if ($LASTEXITCODE -ne 0) { throw 'Publishing the installer failed.' }
$setup = Join-Path $publish "KiteSetup-$version.exe"
Copy-Item (Join-Path $setupOut 'KiteSetup.exe') $setup -Force
Remove-Item $setupOut -Recurse -Force

# 4. Portable zip (with the marker that keeps settings beside the exe)
$portableZip = Join-Path $publish "Kite-$version-portable.zip"
$staging = Join-Path $publish 'portable-staging\Kite'
if (Test-Path (Split-Path $staging)) { Remove-Item (Split-Path $staging) -Recurse -Force }
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item (Join-Path $app '*') $staging -Recurse
Set-Content (Join-Path $staging 'portable') 'Kite keeps its settings, history and skins in this folder while this file exists.'
if (Test-Path $portableZip) { Remove-Item $portableZip -Force }
Compress-Archive -Path $staging -DestinationPath $portableZip -CompressionLevel Optimal
Remove-Item (Split-Path $staging) -Recurse -Force

Get-Item (Join-Path $app 'Kite.exe'), $setup, $portableZip |
    ForEach-Object { "{0,-34} {1,8:N2} MB" -f $_.Name, ($_.Length / 1MB) }
