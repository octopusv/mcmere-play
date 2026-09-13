#requires -Version 7.0
[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$DotnetPath = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$DotnetPath) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    $DotnetPath = if ($command) { $command.Source } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
}
Push-Location $projectRoot
try {
    & $DotnetPath test 'tests\Mcmere.Play.Tests\Mcmere.Play.Tests.csproj' -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }
} finally { Pop-Location }
