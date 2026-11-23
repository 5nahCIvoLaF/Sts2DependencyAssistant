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
if ($null -eq $code) { $code = 0 }

# Treat a present workshop item ID as success: the uploader sometimes
# returns a non-zero code after a successful upload.
$idFile = Join-Path $Workspace 'mod_id.txt'
$id = ""
if (Test-Path -LiteralPath $idFile) {
    $id = (Get-Content -LiteralPath $idFile -Raw).Trim()
}

if ($id -ne "") {
    Write-Host "Upload finished."
    Write-Host "Workshop item ID: $id"
    Write-Host "Page: https://steamcommunity.com/sharedfiles/filedetails/?id=$id"
    if ($code -ne 0) {
        Write-Host "Note: uploader returned $code but the item ID is present - treat as success, verify on the page."
    }
    exit 0
}

Write-Host "Upload failed (uploader exit $code). See mod-uploader.log for details."
exit 1
