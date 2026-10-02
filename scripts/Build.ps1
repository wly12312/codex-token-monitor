[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'source\CodexTokenMonitor.csproj'
& dotnet build $projectFile --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
