@echo off
rem Registers pdfview with Windows: it appears under "Open with" for PDF files
rem and in Settings > Default apps. Writes only to HKEY_CURRENT_USER, so it
rem needs no administrator rights. Undo it with uninstall.cmd.

setlocal
cd /d "%~dp0"

if not exist "dist\pdfview.exe" (
  echo dist\pdfview.exe is missing. Run build.cmd first.
  exit /b 1
)

start "" "%~dp0dist\pdfview.exe" --register
