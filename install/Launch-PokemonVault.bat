@echo off
:: Pokemon Vault launcher - opens the app, starting the service first if needed.
:: If the service is already running, no admin prompt appears.

powershell -NoProfile -Command ^
  "$s = Get-Service -Name PokemonVault -ErrorAction SilentlyContinue;" ^
  "if (-not $s) { Write-Host 'PokemonVault service not installed - run Install-PokemonVault.bat first.'; Start-Sleep 5; exit 1 };" ^
  "if ($s.Status -ne 'Running') {" ^
  "  Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile -Command Start-Service PokemonVault';" ^
  "  (Get-Service PokemonVault).WaitForStatus('Running', (New-TimeSpan -Seconds 20));" ^
  "};" ^
  "Start-Process 'http://localhost:5188'"
