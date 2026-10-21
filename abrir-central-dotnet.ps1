$ErrorActionPreference = 'Stop'

$appRoot = $PSScriptRoot
$projectFile = Join-Path $appRoot 'central-dotnet\CentralTickets.Api\CentralTickets.Api.csproj'
$dataRoot = Join-Path $env:LOCALAPPDATA 'CentralTicketsClaude'
$launchFile = Join-Path $dataRoot 'launch-dotnet.json'
$logOut = Join-Path $dataRoot 'server-dotnet.log'
$logErr = Join-Path $dataRoot 'server-dotnet-error.log'
$port = 17862
$baseUrl = "http://127.0.0.1:$port"

if (-not (Test-Path -LiteralPath $projectFile)) { throw 'Projeto C# da Central não encontrado.' }
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
    $process = Start-Process -FilePath $dotnet `
        -ArgumentList @('run', '--no-launch-profile', '--project', "`"$projectFile`"") `
        -WorkingDirectory $appRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $logOut -RedirectStandardError $logErr

    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 250
        $launch = Get-HealthyLaunch
        if ($launch) { break }
        if ($process.HasExited) { break }
    }

    if (-not $launch) { throw "Não foi possível iniciar a API C#. Consulte $logErr" }
}

Start-Process -FilePath "$baseUrl/bootstrap?token=$($launch.token)"
