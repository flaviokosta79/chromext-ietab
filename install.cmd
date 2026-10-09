@echo off
setlocal
rem Instala o HikTab. Sempre pergunta o IP do DVR (Enter = 10.0.45.117).

set "SITES="
set /p "SITES=Endereco do DVR para Sites Confiaveis [Enter = 10.0.45.117]: "
if not defined SITES set "SITES=10.0.45.117"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %SITES%
pause
