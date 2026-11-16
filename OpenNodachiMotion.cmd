@echo off
setlocal
set "APP=%~dp0Builds\Animanager-win-x64-20261114-154509\Animanager.exe"
if not exist "%APP%" (
  echo Missing tested build: %APP%
  pause
  exit /b 1
)
start "" "%APP%" -- "--data-dir=%~dp0Libraries\Nodachi-Motion" --preview-asset=477fa209702e40fc875b2839b93c3604
