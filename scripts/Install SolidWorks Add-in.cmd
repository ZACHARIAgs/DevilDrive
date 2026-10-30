@echo off
rem Double-click to install the DevilDrive add-in for SolidWorks. Close SolidWorks first.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-SolidWorksAddin.ps1" %*
echo.
pause
