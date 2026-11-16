@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_CLI_HOME=%CD%\.runtime\dotnet"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "NUGET_PACKAGES=%USERPROFILE%\.nuget\packages"
if not defined GODOT_MONO_BIN set "GODOT_MONO_BIN=D:\Godot_Mono\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"
if not exist "%GODOT_MONO_BIN%" set "GODOT_MONO_BIN=D:\Godot_Mono\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"
if not exist "%GODOT_MONO_BIN%" (
  echo Godot Mono was not found. Set GODOT_MONO_BIN to the console executable.
  pause
  exit /b 1
)
dotnet build --nologo -p:NuGetAudit=false
if errorlevel 1 (
  pause
  exit /b 1
)
set "GODOT_APP=%GODOT_MONO_BIN:_console.exe=.exe%"
if not exist ".godot\imported" (
  "%GODOT_MONO_BIN%" --headless --editor --path "%CD%" --import --quit
  if errorlevel 1 exit /b 1
)
start "" "%GODOT_APP%" --path "%CD%" --log-file "%CD%\.runtime\app.log"
