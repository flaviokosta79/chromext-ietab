@echo off
setlocal
rem Uso: duplo clique (pergunta o IP do DVR) ou: install.cmd 192.168.0.64 [outros enderecos...]

set "SITES=%*"
if not defined SITES set /p "SITES=Endereco do DVR para Sites Confiaveis [Enter = 10.0.45.117]: "
if not defined SITES set "SITES=10.0.45.117"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %SITES%
pause
