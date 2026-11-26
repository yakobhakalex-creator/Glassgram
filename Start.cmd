@echo off
setlocal
cd /d "%~dp0"
set "APP=release\Glassgram.exe"
if not exist "%APP%" "C:\Program Files\dotnet\dotnet.exe" publish src\Glassgram\Glassgram.csproj -c Release -r win-x64 --self-contained true -o release
if exist "%APP%" start "" "%APP%"
