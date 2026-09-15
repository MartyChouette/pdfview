@echo off
rem Builds the two release downloads into release\. Pass a version if the
rem default is not what you are tagging:  package.cmd 1.1.0

setlocal
cd /d "%~dp0"

set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=1.0.0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\package.ps1" -Version %VERSION%
