param(
    [switch]$SomenteMemoria,
    [switch]$Simular
)

$ErrorActionPreference = 'Stop'
$serverUrl = 'http://127.0.0.1:49374'
$aiMemoryFile = Join-Path $PSScriptRoot 'ai-memory-executavel.txt'
$dataFile = Join-Path $PSScriptRoot 'ai-memory-dados.txt'
$monitorFile = Join-Path $PSScriptRoot 'monitor-ai-memory.ps1'
$monitorPidFile = Join-Path $PSScriptRoot 'monitor.pid'
$preferencesFile = Join-Path $PSScriptRoot 'preferencias.json'
$repositoryRoot = 'C:\git'

function Select-Repository {
    if (!(Test-Path -LiteralPath $repositoryRoot -PathType Container)) {
        throw "Pasta de repositorios nao encontrada: $repositoryRoot"
    }

    $repositories = @(Get-ChildItem -LiteralPath $repositoryRoot -Directory -Force |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName '.git') } |
        Sort-Object Name)
    if ($repositories.Count -eq 0) {
        throw "Nenhum repositorio Git encontrado em $repositoryRoot."
    }

    Write-Host "`nEscolha o repositorio para Claude e Codex:" -ForegroundColor Cyan
    for ($index = 0; $index -lt $repositories.Count; $index++) {
        Write-Host ("{0,2} - {1}" -f ($index + 1), $repositories[$index].Name)
    }
    Write-Host ' 0 - Cancelar'

    while ($true) {
        $choice = Read-Host 'Numero do repositorio'
        if ($choice -eq '0') { return $null }
        $number = 0
        if ($choice -match '^[1-9]\d*$' -and [int]::TryParse($choice, [ref]$number) -and $number -le $repositories.Count) {
            return $repositories[$number - 1].FullName
        }
        Write-Warning 'Escolha um numero da lista.'
    }
}

function Test-ServerPort {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect('127.0.0.1', 49374, $null, $null)
        if (!$async.AsyncWaitHandle.WaitOne(400)) { return $false }
        $client.EndConnect($async)
        return $true
    }
    catch { return $false }
    finally { $client.Dispose() }
}

function Test-AiMemoryServer {
    if (!(Test-ServerPort)) { return $false }
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "$serverUrl/admin/status" -TimeoutSec 2
        return ($response.StatusCode -eq 200 -and $response.Content -match 'version')
    }
    catch { return $false }
}

function Start-AgentTerminal {
    param(
        [ValidateSet('claude', 'codex')][string]$Agent,
        [string]$Repository
    )
    $agentPathFile = Join-Path $PSScriptRoot "$Agent-executavel.txt"
    if (!(Test-Path -LiteralPath $agentPathFile)) {
        Write-Warning "$Agent nao esta configurado; abertura ignorada."
        return
    }
    $agentExecutable = (Get-Content -LiteralPath $agentPathFile -Raw -ErrorAction SilentlyContinue).Trim()
    if (!$agentExecutable -or !(Test-Path -LiteralPath $agentExecutable -PathType Leaf)) {
        Write-Warning "$Agent nao esta instalado; abertura ignorada."
        return
    }
    $encodedRepository = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Repository))
    $agentShortcut = Join-Path $PSScriptRoot "$Agent-terminal.lnk"
    if ($Simular) {
        Write-Host "Abriria $Agent em $Repository"
        return
    }
    if (Test-Path -LiteralPath $agentShortcut) {
        # Um atalho por execucao preserva o icone de cada agente e isola
        # sessoes iniciadas quase ao mesmo tempo.
        $shortcutDirectory = Join-Path $env:TEMP 'ClaudeCodexLauncher'
        New-Item -ItemType Directory -Path $shortcutDirectory -Force | Out-Null
        Get-ChildItem -LiteralPath $shortcutDirectory -Filter '*.lnk' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-1) } |
            Remove-Item -Force -ErrorAction SilentlyContinue
        $sessionShortcut = Join-Path $shortcutDirectory ("$Agent-" + [Guid]::NewGuid().ToString('N') + '.lnk')
        $shell = New-Object -ComObject WScript.Shell
        $sourceLink = $shell.CreateShortcut($agentShortcut)
        $link = $shell.CreateShortcut($sessionShortcut)
        $powershell = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $launcher = Join-Path $PSScriptRoot 'abrir-claude.ps1'
        $link.TargetPath = $powershell
        $link.Arguments = '-NoExit -NoProfile -ExecutionPolicy Bypass -File "' + $launcher + '" -Agente ' + $Agent + ' -RepositorioCodificado ' + $encodedRepository
        $link.WorkingDirectory = $Repository
        $link.IconLocation = $sourceLink.IconLocation
        $link.Description = $sourceLink.Description
        $link.WindowStyle = $sourceLink.WindowStyle
        $link.Save()
        Start-Process -FilePath $sessionShortcut -WorkingDirectory $Repository
        return
    }

    # Compatibilidade com instalacoes antigas que ainda nao possuem os atalhos internos.
    $powershell = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $launcher = Join-Path $PSScriptRoot 'abrir-claude.ps1'
    $arguments = '-NoExit -NoProfile -ExecutionPolicy Bypass -File "' + $launcher + '" -Agente ' + $Agent + ' -RepositorioCodificado ' + $encodedRepository
    Start-Process -FilePath $powershell -ArgumentList $arguments -WorkingDirectory $Repository
}

function Test-MonitorRunning {
    if (!(Test-Path -LiteralPath $monitorPidFile)) { return $false }
    $storedPid = (Get-Content -LiteralPath $monitorPidFile -Raw -ErrorAction SilentlyContinue).Trim()
    if ($storedPid -notmatch '^\d+$') { return $false }
    $process = Get-Process -Id ([int]$storedPid) -ErrorAction SilentlyContinue
    return ($process -and $process.ProcessName -eq 'powershell')
}

function Start-Monitor {
    if (!(Test-Path -LiteralPath $monitorFile) -or (Test-MonitorRunning)) { return }
    $powershell = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $monitorFile + '"'
    Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden | Out-Null
}

$openClaude = $true
$openCodex = $true
if (!$SomenteMemoria -and (Test-Path -LiteralPath $preferencesFile)) {
    try {
        $preferences = Get-Content -LiteralPath $preferencesFile -Raw | ConvertFrom-Json
        if ($null -ne $preferences.abrirClaude) { $openClaude = [bool]$preferences.abrirClaude }
        if ($null -ne $preferences.abrirCodex) { $openCodex = [bool]$preferences.abrirCodex }
    }
    catch { Write-Warning 'Preferencias de abertura invalidas; usando o padrao.' }
}

if (!$SomenteMemoria -and ($openClaude -or $openCodex)) {
    $repository = Select-Repository
    if (!$repository) {
        Write-Host 'Abertura cancelada.' -ForegroundColor Yellow
        return
    }
}

if (!(Test-Path -LiteralPath $aiMemoryFile) -or !(Test-Path -LiteralPath $dataFile)) {
    throw 'Configuracao do AI Memory ausente. Execute instalar.cmd novamente.'
}

$aiMemory = (Get-Content -LiteralPath $aiMemoryFile -Raw).Trim()
$dataDir = (Get-Content -LiteralPath $dataFile -Raw).Trim()
if (!(Test-Path -LiteralPath $aiMemory -PathType Leaf)) { throw 'AI Memory mudou de local. Execute instalar.cmd novamente.' }

Start-Monitor

for ($monitorWait = 0; $monitorWait -lt 10 -and !(Test-AiMemoryServer); $monitorWait++) {
    Start-Sleep -Milliseconds 500
}

if (!(Test-AiMemoryServer)) {
    Write-Host 'Iniciando AI Memory...' -ForegroundColor Cyan
    $logDir = Join-Path $dataDir 'launcher-logs'
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $stdout = Join-Path $logDir 'servidor-saida.log'
    $stderr = Join-Path $logDir 'servidor-erros.log'
    $escapedDataDir = $dataDir.Replace('"', '\"')
    $serverArguments = "--data-dir `"$escapedDataDir`" serve --transport http --bind 127.0.0.1:49374"
    if (!$Simular) {
        Start-Process -FilePath $aiMemory -ArgumentList $serverArguments -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr | Out-Null
        for ($attempt = 0; $attempt -lt 30 -and !(Test-AiMemoryServer); $attempt++) {
            Start-Sleep -Milliseconds 500
        }
        if (!(Test-AiMemoryServer)) {
            $detail = if (Test-Path -LiteralPath $stderr) { (Get-Content -LiteralPath $stderr -Tail 8 -ErrorAction SilentlyContinue) -join ' ' } else { '' }
            throw "AI Memory nao respondeu em $serverUrl. $detail"
        }
    }
}

Write-Host "AI Memory ativo em $serverUrl" -ForegroundColor Green
if ($SomenteMemoria) { return }

if ($openClaude) { Start-AgentTerminal -Agent 'claude' -Repository $repository }
if ($openCodex) { Start-AgentTerminal -Agent 'codex' -Repository $repository }
if (!$openClaude -and !$openCodex) {
    Write-Host 'Claude e Codex estao desativados nas preferencias da bandeja. Somente o AI Memory foi iniciado.' -ForegroundColor Yellow
}
