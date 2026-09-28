param(
    [Parameter(Mandatory)][string]$AppRoot,
    [Parameter(Mandatory)][string]$DataRoot,
    [Parameter(Mandatory)][ValidatePattern('^[A-Z]{2,8}[0-9]{2,8}$')][string]$Ticket,
    [Parameter(Mandatory)][ValidatePattern('^[a-f0-9-]{36}$')][string]$RunId
)
$ErrorActionPreference = 'Stop'
$configTask = Get-Content -LiteralPath (Join-Path $DataRoot 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$host.UI.RawUI.WindowTitle = "Claude - $Ticket - Central de Tickets"
& $configTask.node (Join-Path $AppRoot 'central\session-runner.mjs') $DataRoot $Ticket $RunId
Write-Host "`nConversa encerrada. O trabalho foi preservado; voce pode retomar pela Central." -ForegroundColor Cyan
Read-Host 'Pressione Enter para fechar esta janela'
