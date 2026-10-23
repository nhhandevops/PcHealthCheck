@echo off
rem Double-click to check this laptop. The result is saved in the Results folder next to this file.
rem Takes about 3 minutes, plus a few questions. See AUDIT-GUIDE.md.
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0audit-laptop.ps1" %*
echo.
pause
