[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot 'source\tests\Monitor.Tests.csproj'
$outputRoot = Join-Path $projectRoot ('artifacts\tests-' + [Guid]::NewGuid().ToString('N'))
& dotnet run --project $projectFile --configuration $Configuration -- $outputRoot
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
