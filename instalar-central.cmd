@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0instalar-central.ps1" -Abrir
if errorlevel 1 pause
