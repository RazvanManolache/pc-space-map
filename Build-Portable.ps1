[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'PcSpaceMap\PcSpaceMap.csproj'
$outputFolder = Join-Path $projectRoot 'dist\PC-Space-Map'

dotnet publish $projectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $outputFolder `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "Portable build failed with exit code $LASTEXITCODE."
}

$executable = Join-Path $outputFolder 'PC Space Map.exe'
Write-Host "Portable PC Space Map is ready: $executable"
