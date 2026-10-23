@echo off
rem Double-click to install the SSMS Claude Chat extension into SSMS 21/22.
rem Extra arguments are passed through, e.g.  Install.cmd -Quiet
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SsmsExtension.ps1" %*
echo.
pause
