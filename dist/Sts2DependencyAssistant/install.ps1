param(
    [string]$Sts2Path = 'E:\SteamLibrary\steamapps\common\Slay the Spire 2'
)

$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $Sts2Path 'mods\Sts2DependencyAssistant'

Write-Host "Source: $source"
Write-Host "Target: $target"

if (-not (Test-Path -LiteralPath $Sts2Path)) {
    throw "Game directory not found: $Sts2Path"
}

$proc = Get-Process -Name 'SlayTheSpire2' -ErrorAction SilentlyContinue
if ($proc) {
    throw "Game is still running. Exit Slay the Spire 2 completely before installing."
}

if (-not (Test-Path -LiteralPath $target)) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
}

foreach ($name in @('Sts2DependencyAssistant.dll', 'Sts2DependencyAssistant.json')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $target $name) -Force
    Write-Host "Copied: $name"
}

Write-Host "Installation complete. Start the game, enable 'Dependency Assistant' in the Mods menu, then restart."
