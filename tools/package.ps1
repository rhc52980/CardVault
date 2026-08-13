# Builds a release zip of CardVault.
#
#   powershell -ExecutionPolicy Bypass -File tools/package.ps1
#
# The package contains the tracked source *plus* the web UI already built into
# server/wwwroot. That's the point of it: installing then needs only the .NET
# SDK on the target machine, matching BAMF, rather than also requiring Node.js
# just to compile a frontend that was already compiled here.
#
# Nothing gitignored goes in -- the file list comes from git, so node_modules,
# bin, obj and appsettings.Local.json (your API key) are excluded by
# construction rather than by a filter someone has to remember to update.

param([string]$OutputDir = "$env:USERPROFILE\Downloads")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

$version = "0.0.0"
$vm = [regex]::Match((Get-Content (Join-Path $root "server\CardVault.csproj") -Raw), '<Version>\s*([^<]+?)\s*</Version>')
if ($vm.Success) { $version = $vm.Groups[1].Value }

$staging = Join-Path ([IO.Path]::GetTempPath()) "cardvault-pkg-$version"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    Step "Building the web UI"
    Push-Location (Join-Path $root "client")
    try {
        if (-not (Test-Path "node_modules")) { npm install --no-fund --no-audit }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }

    $ui = Join-Path $root "server\wwwroot\index.html"
    if (-not (Test-Path $ui)) { throw "The UI build produced no server\wwwroot\index.html" }

    Step "Staging tracked files"
    Push-Location $root
    try {
        # git archive honours .gitattributes, so shell scripts keep LF and the
        # Windows scripts keep CRLF.
        $tar = Join-Path $staging "src.tar"
        git archive --format=tar -o $tar HEAD
        if ($LASTEXITCODE -ne 0) { throw "git archive failed" }
        tar -xf $tar -C $staging
        Remove-Item $tar
    }
    finally { Pop-Location }

    Step "Adding the prebuilt UI"
    $dest = Join-Path $staging "server\wwwroot"
    Copy-Item (Join-Path $root "server\wwwroot") $dest -Recurse -Force

    # Belt and braces: the file list came from git, but a secret slipping into a
    # package is bad enough to check for outright rather than assume.
    $leaked = Get-ChildItem $staging -Recurse -File -Filter "appsettings.Local.json"
    if ($leaked) { throw "appsettings.Local.json ended up in the package - aborting" }

    if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir | Out-Null }
    $zip = Join-Path $OutputDir "CardVault-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Step "Compressing"
    # Wrapped in a CardVault/ folder so extracting doesn't scatter files about.
    $wrapper = Join-Path ([IO.Path]::GetTempPath()) "cardvault-wrap-$version"
    if (Test-Path $wrapper) { Remove-Item $wrapper -Recurse -Force }
    New-Item -ItemType Directory -Path $wrapper | Out-Null
    Move-Item $staging (Join-Path $wrapper "CardVault")

    # tar.exe (bsdtar, shipped with Windows 10 1803+) rather than
    # Compress-Archive: the latter writes backslash path separators under Windows
    # PowerShell, which is out of spec and mangles extraction on Linux -- where
    # this package may well be installed.
    Push-Location $wrapper
    try {
        tar.exe -a -c -f $zip "CardVault"
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
    Remove-Item $wrapper -Recurse -Force

    $size = [Math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host ""
    Write-Host "CardVault $version packaged: $zip ($size MB)" -ForegroundColor Green
    Write-Host "Installing from it needs only the .NET SDK - the web UI is already built."
}
finally {
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
}
