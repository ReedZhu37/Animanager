@echo off
setlocal
set "APP=%~dp0Builds\Animanager-win-x64-20261114-122753\Animanager.exe"
if not exist "%APP%" (
  echo Missing tested build: %APP%
  pause
  exit /b 1
)
start "" "%APP%" -- "--data-dir=%~dp0Libraries\MovementAnimsetPro-CUE4Parse" --console
