@echo off
rem Double-click to remove the SQL Chat extension from SSMS 21/22.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-SsmsExtension.ps1" %*
echo.
pause
