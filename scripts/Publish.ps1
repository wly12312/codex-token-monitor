[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SelfContained,
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'source\CodexTokenMonitor.csproj'
$flavor = if ($SelfContained) { $Runtime + '-self-contained' } else { 'framework-dependent' }
$outputRoot = Join-Path $projectRoot ('artifacts\publish\' + $flavor)
$publishArgs = @('publish', $projectFile, '--configuration', $Configuration, '--output', $outputRoot, '--nologo')
if ($SelfContained) {
    # Runtime packs come from the official NuGet feed, not a local SDK-specific path.
    $publishArgs += @('--runtime', $Runtime, '--self-contained', 'true', '--source', 'https://api.nuget.org/v3/index.json')
} else {
    $publishArgs += '--no-self-contained'
}
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.en.md') -Destination $outputRoot -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'CONTRIBUTING.md') -Destination $outputRoot -Force
$imageRoot = Join-Path $projectRoot 'docs\images'
if (Test-Path -LiteralPath $imageRoot) {
    $publicDocs = Join-Path $outputRoot 'docs'
    New-Item -ItemType Directory -Path $publicDocs -Force | Out-Null
    Copy-Item -LiteralPath $imageRoot -Destination $publicDocs -Recurse -Force
}
$licenseFile = Join-Path $projectRoot 'LICENSE'
if (Test-Path -LiteralPath $licenseFile) { Copy-Item -LiteralPath $licenseFile -Destination $outputRoot -Force }
Write-Host ('Published to ' + $outputRoot)
