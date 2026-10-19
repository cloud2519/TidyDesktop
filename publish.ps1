# Builds the release: a single self-contained exe plus README, zipped for handing out.
# Output: dist\TidyDesktop-<version>-win-x64.zip
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content TidyDesktop.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "TidyDesktop-$version-win-x64"
$out = Join-Path dist $name

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

$zip = Join-Path dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
Get-Item $zip | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
