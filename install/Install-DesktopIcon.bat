@echo off
:: Creates a "CardVault" shortcut on your Desktop with the card-stack icon.
:: Copies the launcher + icon into the install folder first so the shortcut
:: keeps working across updates, which replace everything else in there.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$app = 'C:\CardVault';" ^
  "$svc = Get-CimInstance Win32_Service -Filter \"Name='CardVault'\" -ErrorAction SilentlyContinue;" ^
  "if ($svc -and $svc.PathName) { $bp = $svc.PathName.Trim(); $exe = if ($bp.StartsWith('\"')) { ($bp -split '\"')[1] } else { ($bp -split ' ')[0] }; $d = Split-Path $exe -Parent; if ($d -and (Test-Path $d)) { $app = $d } };" ^
  "if (-not (Test-Path $app)) { New-Item -ItemType Directory -Path $app | Out-Null };" ^
  "Copy-Item -Path (Join-Path '%~dp0' 'Launch-CardVault.bat') -Destination $app -Force;" ^
  "Copy-Item -Path (Join-Path '%~dp0' 'cardvault.ico') -Destination $app -Force;" ^
  "$desktop = [Environment]::GetFolderPath('Desktop');" ^
  "$ws = New-Object -ComObject WScript.Shell;" ^
  "$sc = $ws.CreateShortcut((Join-Path $desktop 'CardVault.lnk'));" ^
  "$sc.TargetPath = Join-Path $app 'Launch-CardVault.bat';" ^
  "$sc.WorkingDirectory = $app;" ^
  "$sc.IconLocation = (Join-Path $app 'cardvault.ico') + ',0';" ^
  "$sc.Description = 'CardVault - collection tracker';" ^
  "$sc.Save();" ^
  "Write-Host 'Desktop shortcut created.' -ForegroundColor Green"
pause
