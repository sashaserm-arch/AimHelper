@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 exit /b 1
call test.cmd
if errorlevel 1 (
    popd
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0package.ps1"
set "ANGLE_PACKAGE_EXIT=%ERRORLEVEL%"
popd
exit /b %ANGLE_PACKAGE_EXIT%
