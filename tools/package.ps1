# Builds the release download into release\pdfview-<version>-win-x64.zip.
#
# The zip unpacks to a folder holding dist\ plus install.cmd and uninstall.cmd,
# which is the shape install.cmd expects to find itself in.

[CmdletBinding()]
param(
    [string] $Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$package = Join-Path $root 'release\staging'
$dist = Join-Path $package 'dist'
$output = Join-Path $root 'release'

if (Test-Path $package) { Remove-Item -LiteralPath $package -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

Write-Host 'Building...' -ForegroundColor Cyan

dotnet publish src\PdfView\PdfView.csproj -c Release -r win-x64 `
    --self-contained true -p:Version=$Version -o $dist
if ($LASTEXITCODE -ne 0) { throw 'app build failed' }

dotnet publish src\PdfThumb\PdfThumb.csproj -c Release `
    -p:Version=$Version -o (Join-Path $dist 'thumbnail')
if ($LASTEXITCODE -ne 0) { throw 'thumbnail build failed' }

# Nothing in a download should carry debug symbols.
Get-ChildItem $dist -Recurse -Filter *.pdb | Remove-Item -Force

Copy-Item install.cmd, uninstall.cmd, LICENSE, THIRD-PARTY-NOTICES.md $package

$zip = Join-Path $output "pdfview-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path "$package\*" -DestinationPath $zip -CompressionLevel Optimal

Remove-Item -LiteralPath $package -Recurse -Force

Write-Host ''
'{0}  {1:N1} MB' -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB)
Write-Host ''
Write-Host 'Ready in release\.' -ForegroundColor Green
