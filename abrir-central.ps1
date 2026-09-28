param([switch]$SomenteServidor)
$ErrorActionPreference = 'Stop'
$taskData = Join-Path $env:LOCALAPPDATA 'CentralTicketsClaude'
$taskLaunch = Join-Path $taskData 'launch.json'
$taskServer = Join-Path $PSScriptRoot 'central\server.mjs'
$taskNode = (Get-Command node.exe -ErrorAction Stop).Source
New-Item -ItemType Directory -Path $taskData -Force | Out-Null

function Get-CentralAtiva {
    if (-not (Test-Path -LiteralPath $taskLaunch)) { return $null }
    try {
        $taskInfo = Get-Content -LiteralPath $taskLaunch -Raw | ConvertFrom-Json
        if ($taskInfo.app -ne 'central-tickets-claude' -or $taskInfo.port -ne 17861) { return $null }
        $taskHealth = Invoke-RestMethod -Uri "http://127.0.0.1:17861/health" -TimeoutSec 2
        if ($taskHealth.app -eq $taskInfo.app -and $taskHealth.pid -eq $taskInfo.pid) { return $taskInfo }
    } catch { }
    return $null
}

$taskMutex = New-Object System.Threading.Mutex($false, 'Local\CentralTicketsClaudeLauncher')
$taskOwnsMutex = $false
try {
    try { $taskOwnsMutex = $taskMutex.WaitOne(15000) } catch [System.Threading.AbandonedMutexException] { $taskOwnsMutex = $true }
    if (-not $taskOwnsMutex) { throw 'Outra abertura da Central esta em andamento. Tente novamente em alguns segundos.' }
    $taskInfo = Get-CentralAtiva
    if (-not $taskInfo) {
        if (Get-NetTCPConnection -LocalPort 17861 -State Listen -ErrorAction SilentlyContinue) { throw 'A porta local 17861 esta ocupada. Nenhum processo foi encerrado. Confira a instalacao.' }
        $taskArgs = '"' + $taskServer + '"'
        $taskProcess = Start-Process -FilePath $taskNode -ArgumentList $taskArgs -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskData 'server.log') -RedirectStandardError (Join-Path $taskData 'server-error.log')
        for ($taskAttempt = 0; $taskAttempt -lt 40; $taskAttempt++) {
            Start-Sleep -Milliseconds 250
            $taskInfo = Get-CentralAtiva
            if ($taskInfo) { break }
            if ($taskProcess.HasExited) { break }
        }
        if (-not $taskInfo) { throw "Nao foi possivel iniciar a Central. Consulte $taskData\server-error.log" }
    }
    if (-not $SomenteServidor) { Start-Process -FilePath ("http://127.0.0.1:17861/bootstrap?token=" + $taskInfo.token) }
} catch {
    if ($SomenteServidor) { throw }
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Central de Tickets') | Out-Null
    exit 1
} finally {
    if ($taskOwnsMutex) { $taskMutex.ReleaseMutex() }
    $taskMutex.Dispose()
}
