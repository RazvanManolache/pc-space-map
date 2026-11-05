[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
$build = Join-Path $dist '.installer-build'
$payload = Join-Path $build 'payload'
$wixVersion = '5.0.2'
$toolFolder = Join-Path $dist ".installer-tools\wix-$wixVersion"
$wix = Join-Path $toolFolder 'wix.exe'

if (!(Test-Path -LiteralPath $wix -PathType Leaf)) {
    dotnet tool install wix --tool-path $toolFolder --version $wixVersion
    if ($LASTEXITCODE -ne 0) { throw "WiX installation failed with exit code $LASTEXITCODE." }
}

& (Join-Path $PSScriptRoot 'Build-Portable.ps1') -OutputFolder $payload
$app = Get-Item -LiteralPath (Join-Path $payload 'PC Space Map.exe')
$version = ([version]$app.VersionInfo.FileVersion).ToString(3)
# Rebuilding the same release must keep the same MSI product identity.
$sha256 = [Security.Cryptography.SHA256]::Create()
try { $hash = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes("PCSpaceMap MSI x64 $version")) }
finally { $sha256.Dispose() }
$productCode = [guid]::new([byte[]]$hash[0..15])
$msi = Join-Path $dist "PC-Space-Map-$version-x64.msi"
$source = Join-Path $PSScriptRoot 'installer\PCSpaceMap.wxs'
$icon = Join-Path $PSScriptRoot 'PcSpaceMap\Assets\PcSpaceMap.ico'

$utilExtension = "WixToolset.Util.wixext/$wixVersion"
Push-Location $build
try {
    & $wix extension add $utilExtension
    if ($LASTEXITCODE -ne 0) { throw "WiX Util extension setup failed with exit code $LASTEXITCODE." }

    & $wix build $source -arch x64 -ext $utilExtension -d "Payload=$payload" -d "ProductVersion=$version" -d "ProductCode=$productCode" -d "IconFile=$icon" `
        -pdbtype none -o $msi
    if ($LASTEXITCODE -ne 0) { throw "MSI build failed with exit code $LASTEXITCODE." }
}
finally { Pop-Location }

Write-Host "Windows installer is ready: $msi"
