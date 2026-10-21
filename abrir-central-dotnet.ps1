param([switch]$Recompilar, [switch]$SomenteServidor)
$ErrorActionPreference = 'Stop'

$appRoot = $PSScriptRoot
$dataRoot = Join-Path $env:LOCALAPPDATA 'CentralTicketsClaude'
$launchFile = Join-Path $dataRoot 'launch-dotnet.json'
$logOut = Join-Path $dataRoot 'server-dotnet.log'
$logErr = Join-Path $dataRoot 'server-dotnet-error.log'
$port = 17861
$baseUrl = "http://127.0.0.1:$port"

if (-not (Test-Path -LiteralPath (Join-Path $appRoot 'central-dotnet\CentralTickets.Api\CentralTickets.Api.csproj'))) { throw 'Projeto C# da Central não encontrado.' }
if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) { throw 'Instale o SDK .NET 10 antes de iniciar a versão C#.' }
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

function Get-HealthyLaunch {
    if (-not (Test-Path -LiteralPath $launchFile)) { return $null }
    try {
        $launch = Get-Content -LiteralPath $launchFile -Raw | ConvertFrom-Json
        if ($launch.app -ne 'central-tickets-dotnet' -or $launch.port -ne $port) { return $null }
        $health = Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 2
        if ($health.app -eq $launch.app -and $health.pid -eq $launch.pid) { return $launch }
    } catch { }
    return $null
}

$launch = Get-HealthyLaunch
if (-not $launch) {
    if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
        throw "A porta $port está ocupada. Nenhum processo foi encerrado; confira o que está usando essa porta."
    }

    $dotnet = (Get-Command dotnet.exe).Source
    $apiDll = Join-Path $appRoot 'central-dotnet\CentralTickets.Api\bin\Release\net10.0\CentralTickets.Api.dll'
    $apiDirectory = Join-Path $appRoot 'central-dotnet\CentralTickets.Api'
    $requiredBinaries = @(
        $apiDll,
        (Join-Path $appRoot 'central-dotnet\CentralTickets.Hook\bin\Release\net10.0\CentralTickets.Hook.dll'),
        (Join-Path $appRoot 'central-dotnet\CentralTickets.ComputerMcp\bin\Release\net10.0-windows\CentralTickets.ComputerMcp.dll'),
        (Join-Path $appRoot 'central-dotnet\CentralTickets.UsageCollector\bin\Release\net10.0\CentralTickets.UsageCollector.dll'),
        (Join-Path $appRoot 'central-dotnet\CentralTickets.SessionRunner\bin\Release\net10.0\CentralTickets.SessionRunner.dll')
    )
    if ($Recompilar -or @($requiredBinaries | Where-Object { -not (Test-Path -LiteralPath $_) }).Count -gt 0) {
        $solution = Join-Path $appRoot 'CentralTickets.DotNet.sln'
        & $dotnet build $solution --configuration Release --nologo
        if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar $solution. A Central não foi iniciada." }
    }
    $process = Start-Process -FilePath $dotnet `
        -ArgumentList @("`"$apiDll`"") `
        -WorkingDirectory $apiDirectory -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $logOut -RedirectStandardError $logErr

    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 250
        $launch = Get-HealthyLaunch
        if ($launch) { break }
        if ($process.HasExited) { break }
    }

    if (-not $launch) { throw "Não foi possível iniciar a API C#. Consulte $logErr" }
}

if (-not $SomenteServidor) { Start-Process -FilePath "$baseUrl/bootstrap?token=$($launch.token)" }
