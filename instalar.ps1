[CmdletBinding()]
param(
    [switch]$NaoIniciar
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$launcherRoot = Join-Path $env:LOCALAPPDATA 'ClaudeCodexLauncher'
$aiMemoryData = Join-Path $env:LOCALAPPDATA 'ai-memory'
$aiMemoryInstall = Join-Path $env:LOCALAPPDATA 'AI Memory\bin'
$serverUrl = 'http://127.0.0.1:49374'

function Write-Step {
    param([string]$Message)
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}

function Update-ProcessPath {
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = (($machinePath, $userPath) | Where-Object { $_ }) -join ';'
}

function Find-Application {
    param(
        [string[]]$Names,
        [string[]]$Candidates = @()
    )

    foreach ($name in $Names) {
        $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command) { return $command.Source }
    }

    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

function Export-AssociatedIcon {
    param(
        [string[]]$SourceCandidates,
        [string]$Destination
    )

    Add-Type -AssemblyName System.Drawing
    foreach ($source in $SourceCandidates) {
        if (!$source -or !(Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($source)
        if (!$icon) { continue }
        try {
            $stream = [IO.File]::Open($Destination, [IO.FileMode]::Create)
            try { $icon.Save($stream) } finally { $stream.Dispose() }
            return $true
        }
        finally { $icon.Dispose() }
    }
    return $false
}

function Test-NodeAndNpm {
    $node = Find-Application -Names @('node.exe')
    $npm = Find-Application -Names @('npm.cmd') -Candidates @((Join-Path $env:APPDATA 'npm\npm.cmd'))
    if (!$node -or !$npm) { return $false }

    $versionText = (& $node --version 2>$null)
    if ($LASTEXITCODE -ne 0 -or $versionText -notmatch '^v(?<major>\d+)\.') { return $false }
    return ([int]$Matches.major -ge 18)
}

function Install-NodeLts {
    Write-Step 'Node.js/npm ausente ou antigo; instalando a versao LTS'

    $winget = Find-Application -Names @('winget.exe')
    if ($winget) {
        & $winget install --id OpenJS.NodeJS.LTS --exact --accept-package-agreements --accept-source-agreements --silent
        Update-ProcessPath
        if (Test-NodeAndNpm) { return }
        Write-Warning 'O WinGet nao deixou uma versao compativel disponivel. Tentando o instalador oficial do Node.js.'
    }

    $architecture = if ([Environment]::Is64BitOperatingSystem) { 'x64' } else { 'x86' }
    $releaseIndex = Invoke-RestMethod -Uri 'https://nodejs.org/dist/index.json'
    $fileToken = "win-$architecture-msi"
    $release = $releaseIndex | Where-Object { $_.lts -and ($_.files -contains $fileToken) } | Select-Object -First 1
    if (!$release) { throw "Nao foi encontrada uma versao LTS oficial do Node.js para Windows $architecture." }

    $msiName = "node-$($release.version)-$architecture.msi"
    $msiPath = Join-Path $env:TEMP $msiName
    try {
        Invoke-WebRequest -Uri "https://nodejs.org/dist/$($release.version)/$msiName" -OutFile $msiPath
        $signature = Get-AuthenticodeSignature -FilePath $msiPath
        if ($signature.Status -ne 'Valid') {
            throw "A assinatura digital do instalador Node.js nao e valida: $($signature.Status)."
        }

        $process = Start-Process -FilePath 'msiexec.exe' -Verb RunAs -Wait -PassThru -ArgumentList @('/i', "`"$msiPath`"", '/qn', '/norestart')
        if ($process.ExitCode -ne 0) { throw "O instalador do Node.js terminou com o codigo $($process.ExitCode)." }
    }
    finally {
        Remove-Item -LiteralPath $msiPath -Force -ErrorAction SilentlyContinue
    }

    Update-ProcessPath
    if (!(Test-NodeAndNpm)) { throw 'Node.js/npm foi instalado, mas ainda nao esta disponivel. Reinicie o Windows e execute instalar.cmd novamente.' }
}

function Ensure-NpmCli {
    param(
        [string]$DisplayName,
        [string]$CommandName,
        [string]$PackageName
    )

    $candidates = @(
        (Join-Path $env:APPDATA "npm\$CommandName.cmd"),
        (Join-Path $env:USERPROFILE ".local\bin\$CommandName.exe")
    )
    $executable = Find-Application -Names @("$CommandName.exe", "$CommandName.cmd") -Candidates $candidates
    if (!$executable) {
        Write-Step "$DisplayName nao encontrado; instalando pelo npm"
        $npm = Find-Application -Names @('npm.cmd') -Candidates @((Join-Path $env:APPDATA 'npm\npm.cmd'))
        if (!$npm) { throw 'npm nao encontrado mesmo depois da instalacao do Node.js.' }
        & $npm install --global "$PackageName@latest"
        if ($LASTEXITCODE -ne 0) { throw "Falha ao instalar $DisplayName pelo npm." }
        Update-ProcessPath
        $executable = Find-Application -Names @("$CommandName.exe", "$CommandName.cmd") -Candidates $candidates
    }

    if (!$executable) { throw "$DisplayName foi instalado, mas o comando $CommandName nao foi localizado." }
    Write-Host "$DisplayName encontrado: $executable" -ForegroundColor Green
    return $executable
}

function Ensure-Git {
    if (Find-Application -Names @('git.exe')) { return }

    Write-Step 'Git for Windows ausente; instalando dependencia recomendada'
    $winget = Find-Application -Names @('winget.exe')
    if (!$winget) {
        Write-Warning 'WinGet nao esta disponivel. O Claude pode usar PowerShell, mas instale o Git for Windows depois para melhor compatibilidade.'
        return
    }

    & $winget install --id Git.Git --exact --accept-package-agreements --accept-source-agreements --silent
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao instalar o Git for Windows.' }
    Update-ProcessPath
}

function Find-AiMemory {
    $candidates = @(
        (Join-Path $aiMemoryInstall 'ai-memory.exe'),
        'C:\Tools\ai-memory\ai-memory.exe',
        (Join-Path $env:USERPROFILE 'bin\ai-memory.exe'),
        (Join-Path $env:LOCALAPPDATA 'ai-memory\ai-memory.exe')
    )
    return Find-Application -Names @('ai-memory.exe') -Candidates $candidates
}

function Install-AiMemory {
    if (![Environment]::Is64BitOperatingSystem) {
        throw 'O AI Memory fornece binario nativo apenas para Windows x64. Use WSL2 em Windows de 32 bits.'
    }

    Write-Step 'AI Memory ausente; baixando o binario nativo oficial para Windows'
    $releaseBase = 'https://github.com/akitaonrails/ai-memory/releases/latest/download'
    $archiveName = 'ai-memory-windows-x86_64.zip'
    $archivePath = Join-Path $env:TEMP $archiveName
    $checksumPath = "$archivePath.sha256"
    $extractPath = Join-Path $env:TEMP ("ai-memory-" + [Guid]::NewGuid().ToString('N'))

    try {
        Invoke-WebRequest -Uri "$releaseBase/$archiveName" -OutFile $archivePath
        Invoke-WebRequest -Uri "$releaseBase/$archiveName.sha256" -OutFile $checksumPath
        $checksumText = Get-Content -LiteralPath $checksumPath -Raw
        if ($checksumText -notmatch '(?i)(?<hash>[a-f0-9]{64})') { throw 'O checksum publicado do AI Memory nao foi reconhecido.' }
        $expected = $Matches.hash.ToLowerInvariant()
        $actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw 'O arquivo do AI Memory falhou na verificacao SHA256.' }

        New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
        Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force
        $downloadedExe = Get-ChildItem -LiteralPath $extractPath -Recurse -Filter 'ai-memory.exe' -File | Select-Object -First 1
        if (!$downloadedExe) { throw 'O pacote baixado nao contem ai-memory.exe.' }

        New-Item -ItemType Directory -Path $aiMemoryInstall -Force | Out-Null
        $packageRoot = $downloadedExe.Directory.FullName
        Copy-Item -Path (Join-Path $packageRoot '*') -Destination $aiMemoryInstall -Recurse -Force
        Get-ChildItem -LiteralPath $aiMemoryInstall -Recurse -File | Unblock-File
    }
    finally {
        Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $checksumPath -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $extractPath) {
            Remove-Item -LiteralPath $extractPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if (($userPath -split ';') -notcontains $aiMemoryInstall) {
        $newUserPath = (($userPath, $aiMemoryInstall) | Where-Object { $_ }) -join ';'
        [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
    }
    Update-ProcessPath

    $installed = Find-AiMemory
    if (!$installed) { throw 'AI Memory foi extraido, mas ai-memory.exe nao foi localizado.' }
    return $installed
}

function Invoke-AiMemory {
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [string]$FailureMessage
    )
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FailureMessage (codigo $LASTEXITCODE)." }
}

Write-Step 'Validando Node.js e npm'
if (!(Test-NodeAndNpm)) { Install-NodeLts }
$nodeVersion = & (Find-Application -Names @('node.exe')) --version
$npmVersion = & (Find-Application -Names @('npm.cmd')) --version
Write-Host "Node.js $nodeVersion | npm $npmVersion" -ForegroundColor Green

Ensure-Git
$claudeExecutable = Ensure-NpmCli -DisplayName 'Claude Code' -CommandName 'claude' -PackageName '@anthropic-ai/claude-code'
$codexExecutable = Ensure-NpmCli -DisplayName 'Codex CLI' -CommandName 'codex' -PackageName '@openai/codex'

$aiMemoryExecutable = Find-AiMemory
if (!$aiMemoryExecutable) { $aiMemoryExecutable = Install-AiMemory }
Write-Host "AI Memory encontrado: $aiMemoryExecutable" -ForegroundColor Green

Write-Step 'Preparando o AI Memory'
New-Item -ItemType Directory -Path $aiMemoryData -Force | Out-Null
$databasePath = Join-Path $aiMemoryData 'db\memory.sqlite'
if (!(Test-Path -LiteralPath $databasePath)) {
    Invoke-AiMemory -Executable $aiMemoryExecutable -Arguments @('--data-dir', $aiMemoryData, 'init') -FailureMessage 'Falha ao inicializar o AI Memory'
}

Write-Step 'Instalando o inicializador local'
New-Item -ItemType Directory -Path $launcherRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'claude.ico') -Destination (Join-Path $launcherRoot 'claude.ico') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'codex.ico') -Destination (Join-Path $launcherRoot 'codex.ico') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'abrir-claude.ps1') -Destination (Join-Path $launcherRoot 'abrir-claude.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'iniciar-agentes.ps1') -Destination (Join-Path $launcherRoot 'iniciar-agentes.ps1') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'monitor-ai-memory.ps1') -Destination (Join-Path $launcherRoot 'monitor-ai-memory.ps1') -Force
Set-Content -LiteralPath (Join-Path $launcherRoot 'claude-executavel.txt') -Value $claudeExecutable -Encoding UTF8
Set-Content -LiteralPath (Join-Path $launcherRoot 'codex-executavel.txt') -Value $codexExecutable -Encoding UTF8
Set-Content -LiteralPath (Join-Path $launcherRoot 'ai-memory-executavel.txt') -Value $aiMemoryExecutable -Encoding UTF8
Set-Content -LiteralPath (Join-Path $launcherRoot 'ai-memory-dados.txt') -Value $aiMemoryData -Encoding UTF8
$preferencesPath = Join-Path $launcherRoot 'preferencias.json'
if (!(Test-Path -LiteralPath $preferencesPath)) {
    [ordered]@{ abrirClaude = $true; abrirCodex = $true } | ConvertTo-Json | Set-Content -LiteralPath $preferencesPath -Encoding UTF8
}

# Prefere os icones embutidos nos aplicativos instalados. Os arquivos do pacote
# continuam como fallback portatil para maquinas sem os aplicativos graficos.
$claudeIconSources = @(
    $(if ([IO.Path]::GetExtension($claudeExecutable) -eq '.exe') { $claudeExecutable }),
    (Join-Path $env:APPDATA 'npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe'),
    (Join-Path $env:USERPROFILE '.local\bin\claude.exe')
)
[void](Export-AssociatedIcon -SourceCandidates $claudeIconSources -Destination (Join-Path $launcherRoot 'claude.ico'))

$codexIconSources = @()
try {
    $codexPackage = Get-AppxPackage -Name 'OpenAI.Codex' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($codexPackage) { $codexIconSources += (Join-Path $codexPackage.InstallLocation 'app\ChatGPT.exe') }
}
catch { }
[void](Export-AssociatedIcon -SourceCandidates $codexIconSources -Destination (Join-Path $launcherRoot 'codex.ico'))

# O servidor precisa estar de pe antes de registrar e testar MCP/hooks.
& (Join-Path $launcherRoot 'iniciar-agentes.ps1') -SomenteMemoria
if ($LASTEXITCODE -ne 0) { throw 'Nao foi possivel iniciar o servidor AI Memory.' }

Write-Step 'Conectando AI Memory ao Claude e ao Codex'
foreach ($integration in @(
    @{ Type = 'mcp'; Client = 'claude-code' },
    @{ Type = 'hooks'; Client = 'claude-code' },
    @{ Type = 'mcp'; Client = 'codex' },
    @{ Type = 'hooks'; Client = 'codex' }
)) {
    if ($integration.Type -eq 'mcp') {
        Invoke-AiMemory -Executable $aiMemoryExecutable -Arguments @('--data-dir', $aiMemoryData, 'install-mcp', '--client', $integration.Client, '--apply') -FailureMessage "Falha ao instalar MCP para $($integration.Client)"
    }
    else {
        Invoke-AiMemory -Executable $aiMemoryExecutable -Arguments @('--data-dir', $aiMemoryData, 'install-hooks', '--agent', $integration.Client, '--apply') -FailureMessage "Falha ao instalar hooks para $($integration.Client)"
    }
}

Write-Step 'Criando o atalho na Area de Trabalho'
$linkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Claude e Codex Administrador.lnk'
$ws = New-Object -ComObject WScript.Shell
$link = $ws.CreateShortcut($linkPath)
$link.TargetPath = Join-Path $env:windir 'System32\cmd.exe'
$psExe = Join-Path $env:windir 'System32\WindowsPowerShell\v1.0\powershell.exe'
$link.Arguments = '/d /c ""' + $psExe + '" -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $launcherRoot 'iniciar-agentes.ps1') + '""'
$link.WorkingDirectory = 'C:\'
$link.IconLocation = (Join-Path $launcherRoot 'claude.ico') + ',0'
$link.Description = 'Escolhe um repositorio em C:\git e inicia AI Memory, Claude Code e Codex como administrador.'
$link.Save()

# Marca "Executar como administrador" no atalho, mantendo o comportamento anterior.
$bytes = [IO.File]::ReadAllBytes($linkPath)
$bytes[21] = $bytes[21] -bor 32
[IO.File]::WriteAllBytes($linkPath, $bytes)
$check = $ws.CreateShortcut($linkPath)
if ($check.TargetPath -ne $link.TargetPath -or $check.Arguments -ne $link.Arguments) { throw 'Falha ao validar o atalho.' }

# Atalhos internos fazem cada console receber a identidade e o icone do agente.
foreach ($agentShortcut in @(
    @{ Agent = 'claude'; Name = 'Claude Code'; Icon = 'claude.ico' },
    @{ Agent = 'codex'; Name = 'Codex'; Icon = 'codex.ico' }
)) {
    $agentLinkPath = Join-Path $launcherRoot "$($agentShortcut.Agent)-terminal.lnk"
    $agentLink = $ws.CreateShortcut($agentLinkPath)
    $agentLink.TargetPath = Join-Path $env:windir 'System32\cmd.exe'
    $agentLink.Arguments = '/d /k ""' + $psExe + '" -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $launcherRoot 'abrir-claude.ps1') + '" -Agente ' + $agentShortcut.Agent + '"'
    $agentLink.WorkingDirectory = 'C:\'
    $agentLink.IconLocation = (Join-Path $launcherRoot $agentShortcut.Icon) + ',0'
    $agentLink.Description = "$($agentShortcut.Name) em um CMD administrador."
    $agentLink.WindowStyle = 1
    $agentLink.Save()
}

$startupLinkPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'AI Memory Monitor.lnk'
$startupLink = $ws.CreateShortcut($startupLinkPath)
$startupLink.TargetPath = $psExe
$startupLink.Arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $launcherRoot 'monitor-ai-memory.ps1') + '"'
$startupLink.WorkingDirectory = $launcherRoot
$startupLink.IconLocation = (Join-Path $launcherRoot 'claude.ico') + ',0'
$startupLink.Description = 'Monitora e reinicia automaticamente o servidor AI Memory.'
$startupLink.WindowStyle = 7
$startupLink.Save()

Write-Host "`nInstalacao concluida." -ForegroundColor Green
Write-Host 'AI Memory esta ativo e integrado ao Claude Code e ao Codex.' -ForegroundColor Green
Write-Host 'Monitor em tempo real instalado nos icones ocultos e configurado para iniciar com o Windows.' -ForegroundColor Green
Write-Host "Atalho criado: $linkPath" -ForegroundColor Green

if (!$NaoIniciar) {
    Write-Host 'Abrindo Claude e Codex. Aceite a solicitacao de administrador do Windows.' -ForegroundColor Yellow
    Start-Process -FilePath $linkPath
}
