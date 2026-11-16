@echo off
setlocal
cd /d "%~dp0"
set "ANIMANAGER_APP=%~dp0Builds\Animanager-win-x64-20261114-093228\Animanager.exe"
if not exist "%ANIMANAGER_APP%" (
    echo The tested Windows build is missing. Run Launch.cmd and use the MovementAnimsetPro library path.
    pause
    exit /b 1
)
start "" "%ANIMANAGER_APP%" -- "--data-dir=%~dp0Libraries\MovementAnimsetPro"
