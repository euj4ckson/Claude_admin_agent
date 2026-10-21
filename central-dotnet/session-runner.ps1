param(
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [Parameter(Mandatory = $true)][string]$TicketId,
    [Parameter(Mandatory = $true)][string]$RunId
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'CentralTickets.SessionRunner\bin\Release\net10.0\CentralTickets.SessionRunner.dll'
if (-not (Test-Path -LiteralPath $runner)) {
    [Console]::Error.WriteLine('Runner .NET da sessão ausente. Execute instalar-central.cmd novamente.')
    exit 2
}

$dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnet) {
    [Console]::Error.WriteLine('O .NET 10 não está instalado ou não está no PATH.')
    exit 2
}

& $dotnet.Source $runner $DataRoot $TicketId $RunId
exit $LASTEXITCODE
