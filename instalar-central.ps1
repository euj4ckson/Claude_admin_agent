param([switch]$Abrir)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Refresh-Path {
    $env:Path = (([Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User')) | Where-Object { $_ }) -join ';'
}

function Get-DotNetSdk10 {
    $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($dotnet) {
        $sdk = & $dotnet.Source --list-sdks | Where-Object { $_ -match '^10\.\d+\.\d+' } | Select-Object -First 1
        if ($sdk) { return $dotnet.Source }
    }

    $winget = Get-Command winget.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $winget) { throw 'Instale o .NET 10 SDK pelo site oficial da Microsoft e execute este instalador novamente.' }
    & $winget.Source install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements --silent
    if ($LASTEXITCODE -ne 0) { throw "A instalação do .NET 10 SDK falhou (código $LASTEXITCODE)." }
    Refresh-Path
    $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dotnet -or -not (& $dotnet.Source --list-sdks | Where-Object { $_ -match '^10\.\d+\.\d+' } | Select-Object -First 1)) {
        throw 'O .NET 10 SDK não ficou disponível. Feche e reabra o terminal e tente novamente.'
    }
    return $dotnet.Source
}

$appRoot = $PSScriptRoot
$dotnet = Get-DotNetSdk10
$projects = @(
    (Join-Path $appRoot 'central-dotnet\CentralTickets.Api\CentralTickets.Api.csproj'),
    (Join-Path $appRoot 'central-dotnet\CentralTickets.Hook\CentralTickets.Hook.csproj'),
    (Join-Path $appRoot 'central-dotnet\CentralTickets.ComputerMcp\CentralTickets.ComputerMcp.csproj'),
    (Join-Path $appRoot 'central-dotnet\CentralTickets.SessionRunner\CentralTickets.SessionRunner.csproj'),
    (Join-Path $appRoot 'central-dotnet\CentralTickets.UsageCollector\CentralTickets.UsageCollector.csproj')
)
foreach ($project in $projects) {
    if (-not (Test-Path -LiteralPath $project)) { throw "Projeto .NET não encontrado: $project" }
    & $dotnet build $project --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Falha ao preparar $project. Nenhuma Central foi iniciada." }
}

$launcher = Join-Path $appRoot 'abrir-central-dotnet.ps1'
$desktop = [Environment]::GetFolderPath('Desktop')
$shortcutPath = Join-Path $desktop 'Central de Tickets.lnk'
$shell = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $shortcutPath) {
    $existing = $shell.CreateShortcut($shortcutPath)
    $legacyLauncher = Join-Path $appRoot 'abrir-central.ps1'
    $belongsToThisInstall = $existing.Arguments.IndexOf($launcher, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $existing.Arguments.IndexOf($legacyLauncher, [StringComparison]::OrdinalIgnoreCase) -ge 0
    if (-not $belongsToThisInstall) {
        throw 'Já existe um atalho "Central de Tickets" apontando para outro local. Ele foi preservado.'
    }
}
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $launcher + '"'
$shortcut.WorkingDirectory = $appRoot
$shortcut.WindowStyle = 7
$shortcut.Description = 'Central local de tickets em ASP.NET Core'
$icon = Join-Path $appRoot 'claude.ico'
if (Test-Path -LiteralPath $icon) { $shortcut.IconLocation = $icon }
$shortcut.Save()

Write-Host "Central .NET preparada e atalho instalado em: $shortcutPath"
Write-Host 'A aplicação usa ASP.NET Core local; Claude Code e Codex CLI são instalados e autenticados separadamente.'
if ($Abrir) { & $launcher }
