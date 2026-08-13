@echo off
:: CardVault launcher - opens the app, starting the service first if needed.
:: If the service is already running, no admin prompt appears.

powershell -NoProfile -Command ^
  "$s = Get-Service -Name CardVault -ErrorAction SilentlyContinue;" ^
  "if (-not $s) { Write-Host 'CardVault service not installed - run Install-CardVault.bat first.'; Start-Sleep 5; exit 1 };" ^
  "if ($s.Status -ne 'Running') {" ^
  "  Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile -Command Start-Service CardVault';" ^
  "  (Get-Service CardVault).WaitForStatus('Running', (New-TimeSpan -Seconds 20));" ^
  "};" ^
  "Start-Process 'http://localhost:5188'"
