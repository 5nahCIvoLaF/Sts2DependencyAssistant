param(
    [string]$Workspace = "",
    [string]$SteamPath = ""
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Workspace)) {
    $Workspace = Join-Path $root 'workshop'
}
$uploader = Join-Path $root 'tools\ModUploader.exe'

Write-Host "Workspace: $Workspace"
Write-Host "Uploader : $uploader"

if (-not (Test-Path -LiteralPath $uploader)) {
    throw "ModUploader.exe not found. Download it first: https://github.com/megacrit/sts2-mod-uploader/releases"
}

if (-not (Test-Path -LiteralPath (Join-Path $Workspace 'workshop.json'))) {
    throw "workshop.json not found in workspace: $Workspace"
}

$content = Join-Path $Workspace 'content'
if (-not (Test-Path -LiteralPath $content)) {
    throw "content directory not found: $content"
}

$image = Join-Path $Workspace 'image.png'
if (-not (Test-Path -LiteralPath $image)) {
    Write-Warning "image.png not found in workspace. Steam requires a cover image under 1MB."
} else {
    $sizeMb = (Get-Item -LiteralPath $image).Length / 1MB
    if ($sizeMb -ge 1) {
        throw ("image.png is {0:N2} MB, must be under 1 MB." -f $sizeMb)
    }
    Write-Host ("Cover image: {0:N0} KB" -f ($sizeMb * 1024))
}

$steam = Get-Process -Name 'steam' -ErrorAction SilentlyContinue
if (-not $steam) {
    Write-Warning "Steam client does not appear to be running. Start Steam and sign in before uploading."
}

Write-Host "Uploading... Steam must be running and the account must own Slay the Spire 2."
& $uploader upload -w $Workspace
$code = $LASTEXITCODE

if ($code -eq 0) {
    $idFile = Join-Path $Workspace 'mod_id.txt'
    if (Test-Path -LiteralPath $idFile) {
        Write-Host ("Workshop item ID: " + (Get-Content -LiteralPath $idFile -Raw).Trim())
    }
    Write-Host "Upload finished."
} else {
    Write-Host "Upload failed with exit code $code. See mod-uploader.log for details."
}

exit $code
