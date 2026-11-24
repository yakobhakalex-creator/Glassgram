@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_CLI_USE_MSBUILD_SERVER=0"
"C:\Program Files\dotnet\dotnet.exe" build src\Glassgram\Glassgram.csproj -c Debug --disable-build-servers
if errorlevel 1 pause
