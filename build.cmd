@echo off
rem Builds pdfview.exe into dist\.
rem
rem   build.cmd           standalone: carries its own .NET, runs anywhere
rem   build.cmd light     small: needs the .NET 8 Desktop Runtime installed
rem
rem Needs the .NET SDK (https://dotnet.microsoft.com/download) and the Edge
rem WebView2 runtime, which ships with Windows 11.

setlocal
cd /d "%~dp0"

if /i "%~1"=="light" (
  set "SELFCONTAINED=false"
  echo Building pdfview ^(needs the .NET 8 Desktop Runtime^)...
) else (
  set "SELFCONTAINED=true"
  echo Building pdfview ^(standalone^)...
)

dotnet publish src\PdfView\PdfView.csproj -c Release -r win-x64 --self-contained %SELFCONTAINED% -o dist
if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)

rem The Explorer thumbnail handler. It lives in its own folder because COM
rem hosting cannot be self-contained and must not mix with the app's runtime.
rem Windows keeps this DLL loaded in dllhost.exe once it has drawn a thumbnail,
rem so if this step fails with "file in use", run uninstall.cmd first.
dotnet publish src\PdfThumb\PdfThumb.csproj -c Release -o dist\thumbnail
if errorlevel 1 (
  echo.
  echo The app built, but the thumbnail handler did not.
  exit /b 1
)

echo.
echo Built dist\pdfview.exe
echo Run install.cmd to add it to "Open with" and turn on PDF thumbnails.
