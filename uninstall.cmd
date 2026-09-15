@echo off
rem Removes the Windows registration made by install.cmd. Leaves dist\ alone.

setlocal
cd /d "%~dp0"

if not exist "dist\pdfview.exe" (
  echo dist\pdfview.exe is missing; nothing to unregister.
  exit /b 1
)

start "" "%~dp0dist\pdfview.exe" --unregister
