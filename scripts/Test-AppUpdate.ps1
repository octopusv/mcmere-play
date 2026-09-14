#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviousSetupPath,[Parameter(Mandatory)][string]$UpdateSetupPath,
    [Parameter(Mandatory)][string]$SignedManifestPath,[Parameter(Mandatory)][string]$DataDirectory,[string]$DotnetPath='', [switch]$NativeParent, [switch]$RelocateBeforeUpdate)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($DataDirectory)
if ($root -notmatch '[\\/]\.test-data[\\/]' -or (Test-Path -LiteralPath $root)) { throw 'Use a new isolated .test-data directory.' }
if (!$DotnetPath) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
$projectRoot = Split-Path $PSScriptRoot -Parent
$installedRoot = Join-Path $root '更新 installed play'
$evidence = Join-Path $root 'evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
function Start-TestProcess([string]$Executable,[string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($Executable))
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($value in $Arguments) { $start.ArgumentList.Add($value) }
    $process = [Diagnostics.Process]::Start($start)
    try { if (!$process.WaitForExit(120000)) { throw "Test process is still running: $($process.Id)" }; if ($process.ExitCode -ne 0) { throw "Test process failed: $($process.ExitCode)" } }
    finally { $process.Dispose() }
}
$installReport = Join-Path $evidence 'install.json'
Start-TestProcess $PreviousSetupPath @('--test-mode','--install','--data-root',$installedRoot,'--report',$installReport)
if (!(Get-Content -LiteralPath $installReport -Raw -Encoding UTF8 | ConvertFrom-Json).success) { throw 'Previous version installation failed.' }
$game = Join-Path $installedRoot 'prism-data/instances/fixture/.minecraft/saves/keep.txt'
New-Item -ItemType Directory -Path (Split-Path $game -Parent) -Force | Out-Null
[IO.File]::WriteAllText($game,'keep game data')
$settings='{"schemaVersion":1,"theme":"dark","servers":[]}'
[IO.File]::WriteAllText((Join-Path $installedRoot 'settings.json'),$settings,[Text.UTF8Encoding]::new($false))
$activeDataRoot=$installedRoot
if ($RelocateBeforeUpdate) {
    $activeDataRoot=Join-Path $root '移行後 data'
    $migrationReport=Join-Path $evidence 'migration.json'
    Start-TestProcess (Join-Path $installedRoot 'app/mcmere-play.exe') @('--smoke-test','--smoke-migrate-to',$activeDataRoot,'--output',$migrationReport)
    $migration=Get-Content -LiteralPath $migrationReport -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$migration.success -or $migration.detail.dataRoot -ine $activeDataRoot) { throw 'Data relocation before update failed.' }
    $game=Join-Path $activeDataRoot 'prism-data/instances/fixture/.minecraft/saves/keep.txt'
}
$updateReport=Join-Path $evidence 'update.json'
& $DotnetPath build (Join-Path $projectRoot 'tools/Mcmere.Play.UpdateSmoke/Mcmere.Play.UpdateSmoke.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Update probe build failed.' }
$probe=Join-Path $projectRoot 'tools/Mcmere.Play.UpdateSmoke/bin/Release/net8.0/Mcmere.Play.UpdateSmoke.dll'
$probeArguments = @($probe,$installedRoot,[IO.Path]::GetFullPath($SignedManifestPath),[IO.Path]::GetFullPath($UpdateSetupPath),$updateReport)
if ($NativeParent) { $probeArguments += '--prepare-only' }
Start-TestProcess $DotnetPath $probeArguments
$handoff=Get-Content -LiteralPath (Join-Path $installedRoot 'updates/probe-result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$setupProcessId = $handoff.setupProcessId
if ($NativeParent) {
    $parentReport = Join-Path $evidence 'parent.json'
    Start-TestProcess (Join-Path $installedRoot 'app/mcmere-play.exe') @('--smoke-test','--smoke-apply-update','--output',$parentReport)
    $parent = Get-Content -LiteralPath $parentReport -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$parent.success -or !$parent.detail.updateHandoffTested) { throw 'The native app did not hand off the update.' }
    $setupProcessId=$parent.detail.setupProcessId
    $updateReport=$parent.detail.setupReport
}
try { $setupProcess=[Diagnostics.Process]::GetProcessById($setupProcessId) } catch [ArgumentException] { $setupProcess=$null }
if ($setupProcess) { try { if (!$setupProcess.WaitForExit(120000)) { throw "Setup is still running: $($setupProcess.Id)" } } finally { $setupProcess.Dispose() } }
$updated=Get-Content -LiteralPath $updateReport -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$updated.success -or $updated.detail.version -ne $handoff.expectedVersion) { throw 'Signed update failed.' }
if ([IO.File]::ReadAllText($game) -ne 'keep game data' -or [IO.File]::ReadAllText((Join-Path $activeDataRoot 'settings.json')) -ne $settings) { throw 'Update changed user data.' }
$launchReport=Join-Path $evidence 'launch.json'
Start-TestProcess (Join-Path $installedRoot 'app/mcmere-play.exe') @('--smoke-test','--output',$launchReport)
$launched=Get-Content -LiteralPath $launchReport -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$launched.success -or !$launched.detail.ui.text.Contains('mcmere Play ' + $handoff.expectedVersion) -or $launched.detail.dataRoot -ine $activeDataRoot) { throw 'Updated application did not start with the expected version and data location.' }
$result=[ordered]@{success=$true; signedManifestVerified=$true; realSetupHandoff=$true; parentExitWait=$true; nativeParent=$NativeParent.IsPresent; updatedVersion=$handoff.expectedVersion; appLaunched=$true; dataPreserved=$true; dataRelocatedBeforeUpdate=$RelocateBeforeUpdate.IsPresent; transport='synthetic release HTTP responses'; productionWindowsRegistrationTested=$false; realGameConnectionTested=$false}
[IO.File]::WriteAllText((Join-Path $evidence 'result.json'),($result | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$result | ConvertTo-Json
