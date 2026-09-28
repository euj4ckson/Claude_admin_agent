@echo off
setlocal
title Instalador Claude + Codex + AI Memory
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0instalar.ps1"
set "RESULTADO=%ERRORLEVEL%"
if not "%RESULTADO%"=="0" echo Nao foi possivel concluir a instalacao. Veja a mensagem acima.
pause
exit /b %RESULTADO%
