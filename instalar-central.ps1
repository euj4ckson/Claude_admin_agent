param([switch]$Abrir)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Refresh-Path {
    $env:Path = (([Environment]::GetEnvironmentVariable('Path','Machine'),[Environment]::GetEnvironmentVariable('Path','User')) | Where-Object { $_ }) -join ';'
}
function Find-Node { Get-Command node.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 }
function Ensure-Node22 {
    $node=Find-Node
    if($node){$major=[int]((& $node.Source --version).TrimStart('v').Split('.')[0]);if($major -ge 22){return $node.Source}}
    $winget=Get-Command winget.exe -ErrorAction SilentlyContinue
    if($winget){& $winget.Source install --id OpenJS.NodeJS.LTS --exact --accept-package-agreements --accept-source-agreements --silent;Refresh-Path;$node=Find-Node;if($node -and [int]((& $node.Source --version).TrimStart('v').Split('.')[0])-ge 22){return $node.Source}}
    $arch=if([Environment]::Is64BitOperatingSystem){'x64'}else{'x86'}
    $release=(Invoke-RestMethod 'https://nodejs.org/dist/index.json' | Where-Object {$_.lts -and $_.files -contains "win-$arch-msi"} | Select-Object -First 1)
    if(!$release){throw 'Nao foi possivel encontrar o Node.js LTS compativel.'}
    $msi=Join-Path $env:TEMP "node-$($release.version)-$arch.msi"
    try{Invoke-WebRequest "https://nodejs.org/dist/$($release.version)/$([IO.Path]::GetFileName($msi))" -OutFile $msi;$sig=Get-AuthenticodeSignature $msi;if($sig.Status -ne 'Valid'){throw 'A assinatura do instalador Node.js nao e valida.'};$p=Start-Process msiexec.exe -Verb RunAs -Wait -PassThru -ArgumentList @('/i',"`"$msi`"",'/qn','/norestart');if($p.ExitCode -ne 0){throw "Instalacao do Node.js falhou: $($p.ExitCode)."}}finally{Remove-Item $msi -Force -ErrorAction SilentlyContinue}
    Refresh-Path;$node=Find-Node;if(!$node -or [int]((& $node.Source --version).TrimStart('v').Split('.')[0])-lt 22){throw 'Node.js 22 ou superior nao ficou disponivel. Reinicie o Windows e tente novamente.'};return $node.Source
}
$taskNodePath=Ensure-Node22
$taskNpm=Get-Command npm.cmd -CommandType Application -ErrorAction SilentlyContinue
if(!$taskNpm){throw 'npm nao foi encontrado junto com o Node.js.'}
Push-Location $PSScriptRoot
try { & $taskNpm.Source install --ignore-scripts --no-audit --no-fund; if($LASTEXITCODE -ne 0){throw 'Falha ao instalar as dependencias da Central.'} } finally { Pop-Location }
$taskLauncher = Join-Path $PSScriptRoot 'abrir-central.ps1'
$taskNode = $taskNodePath
if (-not (Test-Path -LiteralPath $taskLauncher)) { throw 'O launcher da Central nao foi encontrado.' }
$taskNodeMajor = [int]((& $taskNode.Source --version).TrimStart('v').Split('.')[0])
if ($taskNodeMajor -lt 22) { throw 'A Central precisa do Node.js 22 ou mais recente.' }
$taskDesktop = [Environment]::GetFolderPath('Desktop')
$taskShortcutPath = Join-Path $taskDesktop 'Central de Tickets Claude.lnk'
$taskShell = New-Object -ComObject WScript.Shell
if (Test-Path -LiteralPath $taskShortcutPath) {
    $taskPrevious = $taskShell.CreateShortcut($taskShortcutPath)
    if ($taskPrevious.Arguments -notlike ('*' + $taskLauncher + '*')) { throw 'Ja existe um atalho com esse nome apontando para outro local. Ele foi preservado.' }
}
$taskShortcut = $taskShell.CreateShortcut($taskShortcutPath)
$taskShortcut.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$taskShortcut.Arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $taskLauncher + '"'
$taskShortcut.WorkingDirectory = $PSScriptRoot
$taskShortcut.WindowStyle = 7
$taskShortcut.Description = 'Central local: cadastrar, analisar e retomar tickets no Claude Code'
$taskIcon = Join-Path $PSScriptRoot 'claude.ico'
if (Test-Path -LiteralPath $taskIcon) { $taskShortcut.IconLocation = $taskIcon }
$taskShortcut.Save()
Write-Host "Atalho instalado: $taskShortcutPath"
Write-Host 'Nenhum servico, tarefa agendada, permissao de administrador ou dependencia adicional foi instalado.'
if ($Abrir) { & $taskLauncher }
