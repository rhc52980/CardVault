@echo off
:: Creates a "Pokemon Vault" shortcut on your Desktop with the card-stack icon.
:: Copies the launcher + icon into the install folder first so the shortcut
:: keeps working across updates, which replace everything else in there.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$app = 'C:\PokemonVault';" ^
  "$svc = Get-CimInstance Win32_Service -Filter \"Name='PokemonVault'\" -ErrorAction SilentlyContinue;" ^
  "if ($svc -and $svc.PathName) { $bp = $svc.PathName.Trim(); $exe = if ($bp.StartsWith('\"')) { ($bp -split '\"')[1] } else { ($bp -split ' ')[0] }; $d = Split-Path $exe -Parent; if ($d -and (Test-Path $d)) { $app = $d } };" ^
  "if (-not (Test-Path $app)) { New-Item -ItemType Directory -Path $app | Out-Null };" ^
  "Copy-Item -Path (Join-Path '%~dp0' 'Launch-PokemonVault.bat') -Destination $app -Force;" ^
  "Copy-Item -Path (Join-Path '%~dp0' 'vault.ico') -Destination $app -Force;" ^
  "$desktop = [Environment]::GetFolderPath('Desktop');" ^
  "$ws = New-Object -ComObject WScript.Shell;" ^
  "$sc = $ws.CreateShortcut((Join-Path $desktop 'Pokemon Vault.lnk'));" ^
  "$sc.TargetPath = Join-Path $app 'Launch-PokemonVault.bat';" ^
  "$sc.WorkingDirectory = $app;" ^
  "$sc.IconLocation = (Join-Path $app 'vault.ico') + ',0';" ^
  "$sc.Description = 'Pokemon Vault - collection tracker';" ^
  "$sc.Save();" ^
  "Write-Host 'Desktop shortcut created.' -ForegroundColor Green"
pause
