#requires -Version 7.0
[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$DotnetPath = '', [string]$NpmPath = '', [switch]$SkipDependencyRestore)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$DotnetPath) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    $DotnetPath = if ($command) { $command.Source } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
}
Push-Location $projectRoot
try {
    if (!$NpmPath) {
        $command = Get-Command npm.cmd -ErrorAction SilentlyContinue
        $NpmPath = if ($command) { $command.Source } else { Join-Path $env:LOCALAPPDATA 'Programs\nodejs\npm.cmd' }
    }
    Push-Location (Join-Path $projectRoot 'ui')
    try {
        if (!$SkipDependencyRestore) { & $NpmPath ci; if ($LASTEXITCODE -ne 0) { throw 'UI dependency restoration failed.' } }
        & $NpmPath run build
        if ($LASTEXITCODE -ne 0) { throw 'UI build failed.' }
    } finally { Pop-Location }
    & $DotnetPath test 'tests\Mcmere.Play.Tests\Mcmere.Play.Tests.csproj' -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }
    & $DotnetPath build 'src\Mcmere.Play\Mcmere.Play.csproj' -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
} finally { Pop-Location }
