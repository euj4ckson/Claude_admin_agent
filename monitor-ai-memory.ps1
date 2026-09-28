$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AiMemoryNativeIcon {
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool DestroyIcon(IntPtr handle);
}
'@

$serverUrl = 'http://127.0.0.1:49374'
$aiMemoryFile = Join-Path $PSScriptRoot 'ai-memory-executavel.txt'
$dataFile = Join-Path $PSScriptRoot 'ai-memory-dados.txt'
$pidFile = Join-Path $PSScriptRoot 'monitor.pid'
$preferencesFile = Join-Path $PSScriptRoot 'preferencias.json'
$mutexCreated = $false
$mutex = New-Object Threading.Mutex($true, 'Local\AiMemoryTrayMonitor', [ref]$mutexCreated)
if (!$mutexCreated) { exit 0 }

function Write-MonitorLog {
    param([string]$Message)
    try {
        $logDirectory = Join-Path $dataDir 'launcher-logs'
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        Add-Content -LiteralPath (Join-Path $logDirectory 'monitor.log') -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message" -Encoding UTF8
    }
    catch { }
}

function New-StatusIcon {
    param([System.Drawing.Color]$Color)
    $bitmap = New-Object System.Drawing.Bitmap 16, 16
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $brush = New-Object System.Drawing.SolidBrush $Color
    $border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(75, 75, 75)), 1
    $graphics.FillEllipse($brush, 1, 1, 13, 13)
    $graphics.DrawEllipse($border, 1, 1, 13, 13)
    $handle = $bitmap.GetHicon()
    try { return ([System.Drawing.Icon]::FromHandle($handle).Clone()) }
    finally {
        [void][AiMemoryNativeIcon]::DestroyIcon($handle)
        $border.Dispose()
        $brush.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Get-ServerStatus {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri "$serverUrl/admin/status" -TimeoutSec 2
        if ($response.StatusCode -ne 200) { return $null }
        return ($response.Content | ConvertFrom-Json)
    }
    catch { return $null }
}

function Start-AiMemoryServer {
    try {
        $logDirectory = Join-Path $dataDir 'launcher-logs'
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        $stdout = Join-Path $logDirectory 'servidor-saida.log'
        $stderr = Join-Path $logDirectory 'servidor-erros.log'
        $escapedDataDir = $dataDir.Replace('"', '\"')
        $arguments = "--data-dir `"$escapedDataDir`" serve --transport http --bind 127.0.0.1:49374"
        Start-Process -FilePath $aiMemory -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr | Out-Null
        Write-MonitorLog 'Servidor iniciado pelo monitor.'
        return $true
    }
    catch {
        Write-MonitorLog "Falha ao iniciar o servidor: $($_.Exception.Message)"
        return $false
    }
}

function Get-ValidatedServerProcess {
    $holderFile = Join-Path $dataDir '.serve.lock.holder'
    if (!(Test-Path -LiteralPath $holderFile)) { return $null }
    $holderText = Get-Content -LiteralPath $holderFile -Raw -ErrorAction SilentlyContinue
    if ($holderText -notmatch 'pid=(?<pid>\d+)') { return $null }
    $process = Get-Process -Id ([int]$Matches.pid) -ErrorAction SilentlyContinue
    if (!$process -or $process.ProcessName -ne 'ai-memory') { return $null }
    try {
        if ((Resolve-Path -LiteralPath $process.Path).Path -ne (Resolve-Path -LiteralPath $aiMemory).Path) { return $null }
    }
    catch { return $null }
    return $process
}

function Test-AgentAvailable {
    param([ValidateSet('claude', 'codex')][string]$Agent)
    $agentPathFile = Join-Path $PSScriptRoot "$Agent-executavel.txt"
    if (!(Test-Path -LiteralPath $agentPathFile)) { return $false }
    $agentExecutable = (Get-Content -LiteralPath $agentPathFile -Raw -ErrorAction SilentlyContinue).Trim()
    return ($agentExecutable -and (Test-Path -LiteralPath $agentExecutable -PathType Leaf))
}

function Get-AgentPreferences {
    $defaults = [pscustomobject]@{ abrirClaude = $true; abrirCodex = $true }
    if (!(Test-Path -LiteralPath $preferencesFile)) { return $defaults }
    try {
        $saved = Get-Content -LiteralPath $preferencesFile -Raw | ConvertFrom-Json
        return [pscustomobject]@{
            abrirClaude = if ($null -eq $saved.abrirClaude) { $true } else { [bool]$saved.abrirClaude }
            abrirCodex = if ($null -eq $saved.abrirCodex) { $true } else { [bool]$saved.abrirCodex }
        }
    }
    catch {
        Write-MonitorLog "Preferencias invalidas; usando padrao: $($_.Exception.Message)"
        return $defaults
    }
}

function Save-AgentPreferences {
    $preferences = [ordered]@{
        abrirClaude = [bool]$openClaudeItem.Checked
        abrirCodex = [bool]$openCodexItem.Checked
    }
    $preferences | ConvertTo-Json | Set-Content -LiteralPath $preferencesFile -Encoding UTF8
    $notifyIcon.BalloonTipTitle = 'Preferencias salvas'
    $notifyIcon.BalloonTipText = 'A alteracao sera aplicada na proxima abertura do atalho.'
    $notifyIcon.BalloonTipIcon = 'Info'
    $notifyIcon.ShowBalloonTip(2500)
}

if (!(Test-Path -LiteralPath $aiMemoryFile) -or !(Test-Path -LiteralPath $dataFile)) {
    [System.Windows.Forms.MessageBox]::Show('Execute instalar.cmd novamente para configurar o AI Memory.', 'AI Memory Monitor', 'OK', 'Error') | Out-Null
    $mutex.ReleaseMutex()
    $mutex.Dispose()
    exit 1
}

$aiMemory = (Get-Content -LiteralPath $aiMemoryFile -Raw).Trim()
$dataDir = (Get-Content -LiteralPath $dataFile -Raw).Trim()
if (!(Test-Path -LiteralPath $aiMemory -PathType Leaf)) {
    [System.Windows.Forms.MessageBox]::Show('O executavel do AI Memory nao foi encontrado. Execute instalar.cmd novamente.', 'AI Memory Monitor', 'OK', 'Error') | Out-Null
    $mutex.ReleaseMutex()
    $mutex.Dispose()
    exit 1
}

Set-Content -LiteralPath $pidFile -Value $PID -Encoding ASCII
$greenIcon = New-StatusIcon -Color ([System.Drawing.Color]::FromArgb(39, 174, 96))
$yellowIcon = New-StatusIcon -Color ([System.Drawing.Color]::FromArgb(243, 156, 18))
$redIcon = New-StatusIcon -Color ([System.Drawing.Color]::FromArgb(214, 48, 49))
$notifyIcon = New-Object System.Windows.Forms.NotifyIcon
$notifyIcon.Icon = $yellowIcon
$notifyIcon.Text = 'AI Memory: verificando...'
$notifyIcon.Visible = $true

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$statusItem = New-Object System.Windows.Forms.ToolStripMenuItem 'Status: verificando...'
$statusItem.Enabled = $false
$detailsItem = New-Object System.Windows.Forms.ToolStripMenuItem 'Ver detalhes'
$checkItem = New-Object System.Windows.Forms.ToolStripMenuItem 'Verificar agora'
$restartItem = New-Object System.Windows.Forms.ToolStripMenuItem 'Reiniciar AI Memory'
$openClaudeItem = New-Object System.Windows.Forms.ToolStripMenuItem 'Abrir Claude com o atalho'
$openCodexItem = New-Object System.Windows.Forms.ToolStripMenuItem 'Abrir Codex com o atalho'
$openClaudeItem.CheckOnClick = $true
$openCodexItem.CheckOnClick = $true
$openLogsItem = New-Object System.Windows.Forms.ToolStripMenuItem 'Abrir logs'
$openDataItem = New-Object System.Windows.Forms.ToolStripMenuItem 'Abrir dados'
$exitItem = New-Object System.Windows.Forms.ToolStripMenuItem 'Sair do monitor'
[void]$menu.Items.Add($statusItem)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($detailsItem)
[void]$menu.Items.Add($checkItem)
[void]$menu.Items.Add($restartItem)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($openClaudeItem)
[void]$menu.Items.Add($openCodexItem)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($openLogsItem)
[void]$menu.Items.Add($openDataItem)
[void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
[void]$menu.Items.Add($exitItem)
$notifyIcon.ContextMenuStrip = $menu

$agentPreferences = Get-AgentPreferences
$claudeAvailable = Test-AgentAvailable -Agent 'claude'
$codexAvailable = Test-AgentAvailable -Agent 'codex'
$openClaudeItem.Checked = ($claudeAvailable -and $agentPreferences.abrirClaude)
$openCodexItem.Checked = ($codexAvailable -and $agentPreferences.abrirCodex)
if (!$claudeAvailable) {
    $openClaudeItem.Checked = $false
    $openClaudeItem.Enabled = $false
    $openClaudeItem.Text = 'Abrir Claude com o atalho (nao instalado)'
}
if (!$codexAvailable) {
    $openCodexItem.Checked = $false
    $openCodexItem.Enabled = $false
    $openCodexItem.Text = 'Abrir Codex com o atalho (nao instalado)'
}

$script:lastStatus = $null
$script:lastCheck = Get-Date
$script:consecutiveFailures = 0
$script:lastRestartAttempt = [DateTime]::MinValue
$script:forceCheck = $false
$script:closing = $false

function Set-TrayState {
    param($Status)
    $script:lastCheck = Get-Date
    if ($Status) {
        $wasDown = ($script:lastStatus -eq 'down')
        $script:lastStatus = 'up'
        $script:consecutiveFailures = 0
        $notifyIcon.Icon = $greenIcon
        $observations = [int]$Status.counts.observations
        $statusItem.Text = "Status: ATIVO - $observations observacoes"
        $notifyIcon.Text = "AI Memory ativo - $observations observacoes"
        if ($wasDown) {
            $notifyIcon.BalloonTipTitle = 'AI Memory restaurado'
            $notifyIcon.BalloonTipText = 'O servidor voltou a responder normalmente.'
            $notifyIcon.BalloonTipIcon = 'Info'
            $notifyIcon.ShowBalloonTip(4000)
        }
        return
    }

    $script:lastStatus = 'down'
    $script:consecutiveFailures++
    $notifyIcon.Icon = $redIcon
    $statusItem.Text = "Status: PARADO - tentativa $($script:consecutiveFailures)"
    $notifyIcon.Text = 'AI Memory parado - reinicio automatico ativo'

    if ($script:consecutiveFailures -ge 2 -and ((Get-Date) - $script:lastRestartAttempt).TotalSeconds -ge 8) {
        $script:lastRestartAttempt = Get-Date
        $notifyIcon.Icon = $yellowIcon
        $statusItem.Text = 'Status: REINICIANDO...'
        $notifyIcon.Text = 'AI Memory reiniciando...'
        [void](Start-AiMemoryServer)
    }
}

function Update-TrayStatus {
    Set-TrayState -Status (Get-ServerStatus)
}

function Show-Details {
    $status = Get-ServerStatus
    if ($status) {
        $process = Get-ValidatedServerProcess
        $processText = if ($process) { "$($process.Id)" } else { 'nao identificado' }
        $message = @"
Status: ATIVO
Versao: $($status.version)
Servidor: $serverUrl
PID: $processText
Sessoes: $($status.counts.sessions)
Observacoes: $($status.counts.observations)
Paginas: $($status.counts.pages_latest)
Fila de escrita: $($status.write_queue[0]) / $($status.write_queue[1])
Ultima verificacao: $(Get-Date -Format 'HH:mm:ss')
Abrir Claude pelo atalho: $(if ($openClaudeItem.Checked) { 'sim' } else { 'nao' })
Abrir Codex pelo atalho: $(if ($openCodexItem.Checked) { 'sim' } else { 'nao' })
Dados: $dataDir
"@
        [System.Windows.Forms.MessageBox]::Show($message, 'AI Memory Monitor', 'OK', 'Information') | Out-Null
    }
    else {
        [System.Windows.Forms.MessageBox]::Show("Status: PARADO`nO monitor tentara reiniciar automaticamente.", 'AI Memory Monitor', 'OK', 'Warning') | Out-Null
    }
}

$detailsItem.Add_Click({ Show-Details })
$notifyIcon.Add_DoubleClick({ Show-Details })
$checkItem.Add_Click({ Update-TrayStatus })
$restartItem.Add_Click({
    $restartItem.Enabled = $false
    try {
        $process = Get-ValidatedServerProcess
        if ($process) {
            Stop-Process -Id $process.Id -Force
            $process.WaitForExit(5000)
        }
        Start-Sleep -Milliseconds 500
        $script:lastStatus = 'down'
        $script:consecutiveFailures = 2
        $script:lastRestartAttempt = [DateTime]::MinValue
        Update-TrayStatus
    }
    catch { Write-MonitorLog "Falha no reinicio manual: $($_.Exception.Message)" }
    finally { $restartItem.Enabled = $true }
})
$openClaudeItem.Add_Click({ Save-AgentPreferences })
$openCodexItem.Add_Click({ Save-AgentPreferences })
$openLogsItem.Add_Click({
    $logDirectory = Join-Path $dataDir 'launcher-logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$logDirectory`""
})
$openDataItem.Add_Click({ Start-Process -FilePath 'explorer.exe' -ArgumentList "`"$dataDir`"" })
$exitItem.Add_Click({
    $script:closing = $true
    [System.Windows.Forms.Application]::Exit()
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 2000
$timer.Add_Tick({ Update-TrayStatus })

try {
    Write-MonitorLog "Monitor iniciado (PID $PID)."
    Update-TrayStatus
    $timer.Start()
    [System.Windows.Forms.Application]::Run()
}
catch { Write-MonitorLog "Erro fatal no monitor: $($_.Exception.Message)" }
finally {
    $timer.Stop()
    $timer.Dispose()
    $notifyIcon.Visible = $false
    $notifyIcon.Dispose()
    $menu.Dispose()
    $greenIcon.Dispose()
    $yellowIcon.Dispose()
    $redIcon.Dispose()
    if (Test-Path -LiteralPath $pidFile) {
        $storedPid = (Get-Content -LiteralPath $pidFile -Raw -ErrorAction SilentlyContinue).Trim()
        if ($storedPid -eq "$PID") { Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue }
    }
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
