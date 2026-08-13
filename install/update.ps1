# Pokémon Vault one-click installer/updater
# Usage: double-click Install-PokemonVault.bat (runs this elevated), or:
#   powershell -ExecutionPolicy Bypass -File update.ps1
#
# What it does:
#   1. Finds the source tree this script lives in
#   2. Stops the PokemonVault service / process
#   3. Builds the frontend, then publishes the server into C:\PokemonVault
#   4. Pins the data directory so the service and a manual run agree on it,
#      migrating an existing per-user collection across on first install
#   5. Creates or repoints the service and starts it
#
# Your collection is never inside the install folder, so reinstalling can't
# touch it. The app also snapshots the database itself whenever the version
# changes, before anything else runs.

param([string]$InstallDir = "C:\PokemonVault")

$ErrorActionPreference = "Stop"
$Service = "PokemonVault"
$DataDir = Join-Path $InstallDir "data"
$RepointService = $false

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

try {
    # --- follow an existing install rather than assuming the default ---
    # If the service already runs from somewhere else, building into the default
    # would "succeed" while changing nothing that actually runs.
    $svcInfo = Get-CimInstance Win32_Service -Filter "Name='$Service'" -ErrorAction SilentlyContinue
    if ($svcInfo -and $svcInfo.PathName) {
        $binPath = $svcInfo.PathName.Trim()
        $exePath = if ($binPath.StartsWith('"')) { ($binPath -split '"')[1] } else { ($binPath -split ' ')[0] }
        $existingDir = Split-Path $exePath -Parent
        if ($existingDir -and (Test-Path $existingDir)) {
            if ($existingDir -ne $InstallDir) {
                Step "Service '$Service' runs from $existingDir - updating that folder instead"
                $InstallDir = $existingDir
                $DataDir = Join-Path $InstallDir "data"
            }
        }
        else {
            Step "Service '$Service' points at a missing path - installing to $InstallDir and repointing it"
            $RepointService = $true
        }
    }

    # --- locate the source ---
    $srcRoot = Split-Path $PSScriptRoot -Parent   # ...\Pokemon_Vault\install -> ...\Pokemon_Vault
    if (-not (Test-Path (Join-Path $srcRoot "server\PokemonVault.csproj"))) {
        throw "Can't find server\PokemonVault.csproj next to this script. Run it from inside the repo."
    }
    Step "Using source tree: $srcRoot"

    foreach ($tool in @("dotnet", "npm")) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "$tool not found on PATH. The .NET SDK and Node.js are both needed to build."
        }
    }

    $srcVersion = "unknown"
    $vm = [regex]::Match((Get-Content (Join-Path $srcRoot "server\PokemonVault.csproj") -Raw), '<Version>\s*([^<]+?)\s*</Version>')
    if ($vm.Success) { $srcVersion = $vm.Groups[1].Value }

    $installedVersion = $null
    $exe = Join-Path $InstallDir "PokemonVault.exe"
    if (Test-Path $exe) {
        $pv = (Get-Item $exe).VersionInfo.ProductVersion
        if ($pv) { $installedVersion = ($pv -split '\+')[0].Trim() }
    }
    Step "Installing version $srcVersion$(if ($installedVersion) { " (replacing $installedVersion)" })"

    # --- stop whatever is running ---
    $svc = Get-Service -Name $Service -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Stopped") {
        Step "Stopping service $Service"
        Stop-Service -Name $Service -Force
        $svc.WaitForStatus("Stopped", (New-TimeSpan -Seconds 30))
    }
    Get-Process -Name "PokemonVault" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    # --- build the frontend ---
    # The server has no UI without this: Vite writes the built app into
    # server\wwwroot, which publish then packages.
    Step "Building the web UI"
    Push-Location (Join-Path $srcRoot "client")
    try {
        if (-not (Test-Path "node_modules")) {
            npm install --no-fund --no-audit
            if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE" }
        }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }

    # --- publish the server into the install folder ---
    Step "Building the server (this can take a couple of minutes)"
    Push-Location (Join-Path $srcRoot "server")
    try {
        dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true -o $InstallDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }

    # --- prove the build produced what we expect ---
    if (-not (Test-Path $exe)) { throw "Build finished but $exe is missing - nothing was installed." }
    if (-not (Test-Path (Join-Path $InstallDir "wwwroot\index.html"))) {
        throw "Build finished but $InstallDir\wwwroot is missing - the app would not load."
    }

    # --- pin the data directory ---
    # A service account has its own LOCALAPPDATA, so leaving this to the default
    # would give the service an empty collection while yours sat in your profile.
    # Writing it explicitly makes the service and a manual run agree.
    if (-not (Test-Path $DataDir)) { New-Item -ItemType Directory -Path $DataDir -Force | Out-Null }

    $userData = Join-Path $env:LOCALAPPDATA "PokemonVault"
    if ((Test-Path (Join-Path $userData "vault.db")) -and -not (Test-Path (Join-Path $DataDir "vault.db"))) {
        Step "Copying your existing collection from $userData"
        Copy-Item (Join-Path $userData "*") $DataDir -Recurse -Force
        Step "Copied - the original is left in place as a fallback"
    }

    @{ PokemonVault = @{ DataDirectory = $DataDir } } | ConvertTo-Json |
        Set-Content (Join-Path $InstallDir "appsettings.Production.json") -Encoding utf8
    Step "Data directory pinned to $DataDir"

    # The build deliberately excludes appsettings.Local.json so the API key can't
    # ride along in a publish folder. Carry an existing one across explicitly.
    $localSettings = Join-Path $srcRoot "server\appsettings.Local.json"
    if (Test-Path $localSettings) {
        Copy-Item $localSettings $InstallDir -Force
        Step "Copied your appsettings.Local.json (API key) into the install folder"
    }

    # LocalService can't write under C:\ by default, and the whole app is a
    # database write.
    icacls $DataDir /grant "*S-1-5-19:(OI)(CI)M" /T /Q | Out-Null

    # --- keep the launcher tools inside the install folder ---
    Copy-Item (Join-Path $PSScriptRoot "*") $InstallDir -Force -Include "Launch-PokemonVault.bat", "vault.ico"

    # --- service ---
    $svc = Get-Service -Name $Service -ErrorAction SilentlyContinue
    if (-not $svc) {
        Step "Service not found - creating it"
        sc.exe create $Service binPath= "$exe" start= auto obj= "NT AUTHORITY\LocalService" | Out-Null
        sc.exe description $Service "Pokemon Vault - collection tracker" | Out-Null
    }
    elseif ($RepointService) {
        Step "Repointing service $Service at $exe"
        sc.exe config $Service binPath= "$exe" | Out-Null
    }

    Step "Starting service $Service"
    Start-Service -Name $Service

    # --- report what is actually running ---
    # An update that changed nothing must not claim success.
    $runningFrom = $exe
    $svcNow = Get-CimInstance Win32_Service -Filter "Name='$Service'" -ErrorAction SilentlyContinue
    if ($svcNow -and $svcNow.PathName) {
        $bp = $svcNow.PathName.Trim()
        $runningFrom = if ($bp.StartsWith('"')) { ($bp -split '"')[1] } else { ($bp -split ' ')[0] }
    }

    $ver = (Get-Item $exe).VersionInfo.ProductVersion
    if ($ver) { $ver = ($ver -split '\+')[0].Trim() }

    Write-Host ""
    if ($runningFrom -ne $exe) {
        Write-Host "WARNING: the service runs $runningFrom but this installed to $exe." -ForegroundColor Yellow
        Write-Host "Those are different folders, so the update will not take effect. Fix with:"
        Write-Host "  sc.exe config $Service binPath= `"$exe`""
    }
    else {
        # Give it a moment to bind before pointing anyone at the URL.
        $ok = $false
        foreach ($i in 1..20) {
            try {
                if ((Invoke-WebRequest "http://localhost:5188/api/auth/status" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { $ok = $true; break }
            } catch { Start-Sleep -Seconds 1 }
        }

        Write-Host "Pokemon Vault $ver installed successfully." -ForegroundColor Green
        Write-Host "Running from: $runningFrom"
        Write-Host "Collection:   $DataDir"
        if ($ok) { Write-Host "Open:         http://localhost:5188" }
        else { Write-Host "The service started but hasn't answered yet - check: sc.exe query $Service" -ForegroundColor Yellow }
        Write-Host ""
        Write-Host "It now starts automatically at boot, before you log in." -ForegroundColor Gray
        Write-Host "Consider setting a password under Settings, since it listens on your network." -ForegroundColor Gray
    }
}
catch {
    Write-Host ""
    Write-Host "INSTALL FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Check the service with: sc.exe query PokemonVault"
    exit 1
}
