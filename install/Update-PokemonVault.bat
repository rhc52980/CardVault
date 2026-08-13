@echo off
:: Pokemon Vault updater - double-click me after pulling new code.
:: Rebuilds into wherever the service currently runs from, so your install
:: location and collection are preserved.
::
:: Same script as Install-PokemonVault.bat.

powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0update.ps1\"'"
