@echo off
rem Builds pdfview.exe into dist\, then re-renders any document in docs\ whose
rem HTML is newer than its PDF.
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

call :render_doc docs\INTERNALS

echo.
echo Built dist\pdfview.exe
echo Run install.cmd to add it to "Open with" and turn on PDF thumbnails.
exit /b 0


rem ---------------------------------------------------------------------------
rem Documents are written as HTML and read as PDF. Rendering one needs a
rem Chromium, and this machine already has the one the app itself depends on:
rem Edge. Chrome stands in if Edge is missing.
rem
rem Only re-renders when the HTML is newer. Chromium's PDF output is not
rem byte-identical between runs on the same input, so rendering every time would
rem show up as a 650 KB change in git after every build.
rem
rem Never fails the build. A missing browser leaves the committed PDF alone, and
rem the HTML is the source either way.
rem
rem   %1  path without extension, e.g. docs\INTERNALS
rem ---------------------------------------------------------------------------
:render_doc
setlocal
set "SRC=%~1.html"
set "PDF=%~1.pdf"
if not exist "%SRC%" exit /b 0

set "PF86=%ProgramFiles(x86)%"
set "BROWSER="
for %%B in (
  "%PF86%\Microsoft\Edge\Application\msedge.exe"
  "%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"
  "%ProgramFiles%\Google\Chrome\Application\chrome.exe"
  "%PF86%\Google\Chrome\Application\chrome.exe"
  "%LocalAppData%\Google\Chrome\Application\chrome.exe"
) do if not defined BROWSER if exist %%B set "BROWSER=%%~B"

if not defined BROWSER (
  echo Skipping %PDF%: no Edge or Chrome to render it with.
  exit /b 0
)

rem Not inside an if-block: %STALE% would be expanded when the block is parsed,
rem which is before the for loop has set it.
if not exist "%PDF%" goto :do_render

set "STALE=1"
for /f %%S in ('powershell -NoProfile -Command ^
  "if ((Get-Item '%SRC%').LastWriteTimeUtc -gt (Get-Item '%PDF%').LastWriteTimeUtc) { 1 } else { 0 }"') do set "STALE=%%S"

if "%STALE%"=="0" (
  echo %PDF% is up to date.
  exit /b 0
)

:do_render
echo Rendering %PDF%...

rem Render beside the target and move on success, so a browser that fails
rem halfway cannot leave a truncated PDF where the good one was.
set "TMPPDF=%TEMP%\pdfview-doc-%RANDOM%.pdf"
set "SRCURL=file:///%CD:\=/%/%SRC:\=/%"

"%BROWSER%" --headless=new --disable-gpu --no-first-run --no-default-browser-check ^
  --user-data-dir="%TEMP%\pdfview-docrender" ^
  --no-pdf-header-footer --virtual-time-budget=15000 ^
  --print-to-pdf="%TMPPDF%" "%SRCURL%" >nul 2>&1

if exist "%TMPPDF%" (
  move /y "%TMPPDF%" "%PDF%" >nul
  echo Rendered %PDF%
) else (
  echo Could not render %PDF%; leaving the existing one alone.
)

exit /b 0
