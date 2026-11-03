# Builds both releases from the same exe:
#   dist\TidyDesktop-<version>-win-x64.zip   single exe plus README, for GitHub
#   dist\TidyDesktop-<version>-win-x64.msix  unsigned package for the Microsoft Store
# The Store re-signs the package, so it is uploaded unsigned. Pass the three values shown under
# Partner Center > Product identity; the defaults are placeholders and the Store will reject them.
# -Release also publishes a GitHub release (tag v<version>) with the zip and the bare exe; the app's
# "Check for updates" downloads that exe, so every release must include it.
param(
    [string]$IdentityName = '404architect.TidyDesktop',
    [string]$Publisher = 'CN=2E8F13B0-E0F9-4256-A275-CCBA8BFA0843',
    [string]$PublisherDisplayName = '404architect',
    [string]$OutDir = 'dist',
    [switch]$Release
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content TidyDesktop.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "TidyDesktop-$version-win-x64"
$out = Join-Path $OutDir $name

dotnet publish -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o (Join-Path obj publish-release) -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
# WPF's native parts (wpfgfx, PresentationNative, ...) must be inside the exe; a release that
# needs files next to it fails on other PCs.
$extra = Get-ChildItem (Join-Path obj publish-release) -File | Where-Object Name -ne 'TidyDesktop.exe'
if ($extra) { throw "publish left files beside the exe: $($extra.Name -join ', ')" }
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item (Join-Path "obj\publish-release" "TidyDesktop.exe") $out
foreach ($readme in Get-ChildItem dist-files -Filter 'README*.txt') {
    (Get-Content $readme.FullName -Raw).Replace('{VERSION}', $version) |
        Set-Content (Join-Path $out $readme.Name) -Encoding utf8BOM
}

$zip = Join-Path $OutDir "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Get-Item $zip | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }

if ($Release) {
    gh release create "v$version" $zip (Join-Path obj publish-release TidyDesktop.exe) --title "v$version" --generate-notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
}

# --- Microsoft Store package (MSIX) ---
$makeappx = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $makeappx) { Write-Warning "Windows SDK (makeappx.exe) not found - skipped the MSIX package."; return }

$stage = Join-Path obj msix-stage
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path obj publish-release TidyDesktop.exe) $stage
Copy-Item Package\Assets (Join-Path $stage Assets) -Recurse
(Get-Content Package\AppxManifest.xml -Raw).
    Replace('{IDENTITY_NAME}', $IdentityName).Replace('{PUBLISHER}', $Publisher).
    Replace('{PUBLISHER_DISPLAY_NAME}', $PublisherDisplayName).Replace('{VERSION}', "$version.0") |
    Set-Content (Join-Path $stage AppxManifest.xml) -Encoding utf8

$msix = Join-Path $OutDir "$name.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx.FullName pack /d $stage /p $msix /o | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
Get-Item $msix | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
