@echo off
setlocal
cd /d "%~dp0"
if not defined GODOT_MONO_BIN set "GODOT_MONO_BIN=D:\Godot_Mono\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"
if not exist "%GODOT_MONO_BIN%" set "GODOT_MONO_BIN=D:\Godot_Mono\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe"
if not exist "%GODOT_MONO_BIN%" (
  echo Godot Mono was not found.
  pause
  exit /b 1
)
dotnet build --nologo -p:NuGetAudit=false
if errorlevel 1 (
  pause
  exit /b 1
)
set "GODOT_APP=%GODOT_MONO_BIN:_console.exe=.exe%"
start "" "%GODOT_APP%" --path "%CD%" -- "--data-dir=%CD%\Libraries\Lucy-Female-Corrected"
