# Builds the two release downloads into release\.
#
#   pdfview-<version>-win-x64.zip            runs as is
#   pdfview-<version>-win-x64-framework.zip  needs the .NET 8 Desktop Runtime
#
# Each zip unpacks to a folder holding dist\ plus install.cmd and uninstall.cmd,
# which is the shape install.cmd expects.

[CmdletBinding()]
param(
    [string] $Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$staging = Join-Path $root 'release\staging'
$output = Join-Path $root 'release'

if (Test-Path $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $output | Out-Null

$flavours = @(
    @{ Name = 'win-x64';           SelfContained = 'true';  Suffix = '' }
    @{ Name = 'win-x64-framework'; SelfContained = 'false'; Suffix = '-framework' }
)

foreach ($flavour in $flavours) {
    $dist = Join-Path $staging "$($flavour.Name)\dist"
    $package = Join-Path $staging $flavour.Name

    Write-Host "Building $($flavour.Name)..." -ForegroundColor Cyan

    dotnet publish src\PdfView\PdfView.csproj -c Release -r win-x64 `
        --self-contained $flavour.SelfContained -p:Version=$Version -o $dist
    if ($LASTEXITCODE -ne 0) { throw "app build failed for $($flavour.Name)" }

    dotnet publish src\PdfThumb\PdfThumb.csproj -c Release `
        -p:Version=$Version -o (Join-Path $dist 'thumbnail')
    if ($LASTEXITCODE -ne 0) { throw "thumbnail build failed for $($flavour.Name)" }

    # Nothing in a download should carry debug symbols.
    Get-ChildItem $dist -Recurse -Filter *.pdb | Remove-Item -Force

    Copy-Item install.cmd, uninstall.cmd, LICENSE, THIRD-PARTY-NOTICES.md $package

    $zip = Join-Path $output "pdfview-$Version-win-x64$($flavour.Suffix).zip"
    if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path "$package\*" -DestinationPath $zip -CompressionLevel Optimal
}

Remove-Item -LiteralPath $staging -Recurse -Force

Write-Host ''
Get-ChildItem $output -Filter *.zip | ForEach-Object {
    '{0,-44} {1,7:N1} MB' -f $_.Name, ($_.Length / 1MB)
}
Write-Host ''
Write-Host "Ready in release\. Attach both to the GitHub release." -ForegroundColor Green
