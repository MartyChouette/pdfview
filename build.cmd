@echo off
rem Builds pdfview.exe into dist\.
rem
rem Needs the .NET SDK (https://dotnet.microsoft.com/download) and the Edge
rem WebView2 runtime, which ships with Windows 11.
rem
rem Windows keeps the thumbnail handler loaded once Explorer has drawn a
rem preview, so if this fails with "file in use", run uninstall.cmd first.

setlocal
cd /d "%~dp0"

echo Building pdfview...
dotnet publish src\PdfView\PdfView.csproj -c Release -r win-x64 --self-contained true -o dist
if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)

dotnet publish src\PdfThumb\PdfThumb.csproj -c Release -o dist\thumbnail
if errorlevel 1 (
  echo.
  echo The app built, but the thumbnail handler did not.
  exit /b 1
)

echo.
echo Built dist\pdfview.exe
echo Run install.cmd to add it to "Open with" and turn on PDF thumbnails.
