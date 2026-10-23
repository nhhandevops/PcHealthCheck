@echo off
rem Double-click after checking laptops: builds one table from every result in the Results folder.
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0summarize-fleet.ps1" %*
echo.
pause
