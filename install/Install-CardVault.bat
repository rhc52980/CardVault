@echo off
:: CardVault first-time installer - double-click me.
:: Builds from this source folder into C:\CardVault, creates the
:: CardVault Windows service, and starts it. Needs the .NET SDK and Node.js.
::
:: This runs the same script as Update-CardVault.bat - installing and
:: updating are the same operation. Use whichever name matches what you're doing.

powershell -NoProfile -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%~dp0update.ps1\"'"
