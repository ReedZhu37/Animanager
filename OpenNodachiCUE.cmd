@echo off
setlocal
set "APP=%~dp0Builds\Animanager-win-x64-20261114-131132\Animanager.exe"
if not exist "%APP%" (
  echo Missing tested build: %APP%
  pause
  exit /b 1
)
start "" "%APP%" -- "--data-dir=%~dp0Libraries\Nodachi-CUE4Parse" --preview-asset=9e53405d08014918b1ea2cc7c0e9ab81
