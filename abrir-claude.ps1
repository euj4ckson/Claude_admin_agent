param(
    [ValidateSet('claude', 'codex')]
    [string]$Agente = 'claude',
    [string]$RepositorioCodificado
)

$ErrorActionPreference = 'Stop'
if ($RepositorioCodificado) {
    try {
        $repository = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($RepositorioCodificado))
        $repository = [IO.Path]::GetFullPath($repository)
    }
    catch { throw 'Repositorio informado pelo inicializador e invalido.' }
}
else {
    if (!(Test-Path -LiteralPath 'C:\git' -PathType Container)) { throw 'Pasta C:\git nao encontrada.' }
    $repositories = @(Get-ChildItem -LiteralPath 'C:\git' -Directory -Force |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName '.git') } |
        Sort-Object Name)
    if ($repositories.Count -eq 0) { throw 'Nenhum repositorio Git encontrado em C:\git.' }

    Write-Host "`nEscolha o repositorio para $Agente`n" -ForegroundColor Cyan
    for ($index = 0; $index -lt $repositories.Count; $index++) {
        Write-Host ("{0,2} - {1}" -f ($index + 1), $repositories[$index].Name)
    }
    Write-Host ' 0 - Cancelar'
    while ($true) {
        $choice = Read-Host 'Numero do repositorio'
        if ($choice -eq '0') { return }
        $number = 0
        if ($choice -match '^[1-9]\d*$' -and [int]::TryParse($choice, [ref]$number) -and $number -le $repositories.Count) {
            $repository = $repositories[$number - 1].FullName
            break
        }
        Write-Warning 'Escolha um numero da lista.'
    }
}
if ([IO.Path]::GetDirectoryName($repository.TrimEnd('\')) -ne 'C:\git' -or
    !(Test-Path -LiteralPath $repository -PathType Container) -or
    !(Test-Path -LiteralPath (Join-Path $repository '.git'))) {
    throw "Repositorio Git nao encontrado em C:\git: $repository"
}
Set-Location -LiteralPath $repository

$displayName = if ($Agente -eq 'claude') { 'CLAUDE CODE' } else { 'CODEX CLI' }
$host.UI.RawUI.WindowTitle = "$displayName - $repository"
$pathFile = Join-Path $PSScriptRoot "$Agente-executavel.txt"
if (!(Test-Path -LiteralPath $pathFile)) { throw "Configuracao de $displayName ausente. Execute instalar.cmd novamente." }

$executable = (Get-Content -LiteralPath $pathFile -Raw).Trim()
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw "$displayName mudou de local. Execute instalar.cmd novamente." }

Write-Host "$displayName - $repository" -ForegroundColor Cyan
Write-Host '1 - Nova conversa (padrao)'
Write-Host '2 - Selecionar conversa anterior'
$choice = Read-Host 'Escolha 1 ou 2'

if ($choice -eq '2') {
    if ($Agente -eq 'claude') { & $executable --resume }
    else { & $executable resume }
}
else {
    & $executable
}
