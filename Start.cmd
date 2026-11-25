@echo off
setlocal
cd /d "%~dp0"
set "APP=src\Glassgram\bin\x64\Debug\net11.0-windows10.0.26100.0\win-x64\Glassgram.exe"
if not exist "%APP%" call Build.cmd
if exist "%APP%" start "" "%APP%"
