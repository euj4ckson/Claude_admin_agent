param([switch]$SomenteServidor, [switch]$Recompilar)

$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot 'abrir-central-dotnet.ps1'
if (-not (Test-Path -LiteralPath $launcher)) { throw 'Launcher .NET da Central não encontrado.' }

$arguments = @()
if ($SomenteServidor) { $arguments += '-SomenteServidor' }
if ($Recompilar) { $arguments += '-Recompilar' }
& $launcher @arguments
exit $LASTEXITCODE
