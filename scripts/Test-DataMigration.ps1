#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$DataDirectory,[string]$SetupPath='', [string]$ApplicationPath='', [string]$DotnetPath='',
    [string]$UpgradeSetupPath='', [string]$RuntimeFixtureRoot='')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($DataDirectory)
if ($root -notmatch '[\\/]\.test-data[\\/]' -or (Test-Path -LiteralPath $root)) { throw 'Use a new isolated .test-data directory.' }
if (!$SetupPath -and !$ApplicationPath) { throw 'Specify a Setup or an application to test.' }
$control=Join-Path $root 'control'
$destination=Join-Path $root '移行後 data'
$evidence=Join-Path $root 'evidence'
New-Item -ItemType Directory -Path $control,$evidence -Force | Out-Null
function Run-Application([string]$Executable,[string[]]$Arguments) {
    $executable=[IO.Path]::GetFullPath($Executable)
    if ([IO.Path]::GetExtension($executable) -ieq '.dll') {
        $dotnet=if ($DotnetPath) { $DotnetPath } else { (Get-Command dotnet -ErrorAction Stop).Source }
        $start=[Diagnostics.ProcessStartInfo]::new($dotnet)
        $start.ArgumentList.Add($executable)
    } else { $start=[Diagnostics.ProcessStartInfo]::new($executable) }
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($value in $Arguments) { $start.ArgumentList.Add($value) }
    $process=[Diagnostics.Process]::Start($start)
    try { if (!$process.WaitForExit(120000)) { throw "Process is still running: $($process.Id)" }; if ($process.ExitCode -ne 0) { throw "Migration test process failed: $($process.ExitCode)" } }
    finally { $process.Dispose() }
}
if ($SetupPath) {
    $installReport=Join-Path $evidence 'install.json'
    Run-Application $SetupPath @('--test-mode','--install','--data-root',$control,'--report',$installReport)
    if (!(Get-Content -LiteralPath $installReport -Raw -Encoding UTF8 | ConvertFrom-Json).success) { throw 'Setup failed.' }
    $ApplicationPath=Join-Path $control 'app/mcmere-play.exe'
}
$gameRelative='prism-data/instances/fixture/.minecraft/saves/keep.txt'
$game=Join-Path $control $gameRelative
New-Item -ItemType Directory -Path (Split-Path $game -Parent) -Force | Out-Null
[IO.File]::WriteAllText($game,'synthetic game data to preserve')
$settings='{"schemaVersion":1,"theme":"dark","servers":[]}'
[IO.File]::WriteAllText((Join-Path $control 'settings.json'),$settings,[Text.UTF8Encoding]::new($false))
$account=Join-Path $control 'prism-data/accounts.json'
[IO.File]::WriteAllText($account,'synthetic account fixture; must never be copied')
if ($RuntimeFixtureRoot) {
    $runtimeSource=[IO.Path]::GetFullPath($RuntimeFixtureRoot)
    if ($runtimeSource -notmatch '[\\/]\.test-data[\\/]') { throw 'Only isolated runtime fixtures can be reused.' }
    $javaSource=Join-Path $runtimeSource 'runtimes/java'
    if (!(Test-Path -LiteralPath $javaSource -PathType Container)) { throw 'Java runtime fixture is missing.' }
    New-Item -ItemType Directory -Path (Join-Path $control 'runtimes'),(Join-Path $control 'state') -Force | Out-Null
    Copy-Item -LiteralPath $javaSource -Destination (Join-Path $control 'runtimes/java') -Recurse
    Get-ChildItem -LiteralPath (Join-Path $runtimeSource 'state') -File -Filter 'runtime-temurin-*.json' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $control ('state/'+$_.Name)) }
}
$migrationReport=Join-Path $evidence 'migration.json'
$accountLock=[IO.File]::Open($account,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try { Run-Application $ApplicationPath @('--smoke-test','--data-root',$control,'--smoke-migrate-to',$destination,'--output',$migrationReport) }
finally { $accountLock.Dispose() }
$migrated=Get-Content -LiteralPath $migrationReport -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$migrated.success -or !$migrated.detail.migrationTested -or $migrated.detail.dataRoot -ine $destination -or $migrated.detail.controlRoot -ine $control) { throw 'The native app did not switch data locations.' }
if ($RuntimeFixtureRoot -and !$migrated.detail.migratedJavaVerified) { throw 'Java was not verified at the new location.' }
if ([IO.File]::ReadAllText($game) -ne 'synthetic game data to preserve' -or [IO.File]::ReadAllText((Join-Path $destination $gameRelative)) -ne 'synthetic game data to preserve') { throw 'Game data was changed or lost.' }
if ([IO.File]::ReadAllText((Join-Path $destination 'settings.json')) -ne $settings) { throw 'Settings were changed.' }
if (Test-Path -LiteralPath (Join-Path $destination 'prism-data/accounts.json')) { throw 'Prism credentials were copied.' }
if (!(Test-Path -LiteralPath $account)) { throw 'The original account fixture was removed.' }
$restartReport=Join-Path $evidence 'restart.json'
Run-Application $ApplicationPath @('--smoke-test','--data-root',$control,'--output',$restartReport)
$restarted=Get-Content -LiteralPath $restartReport -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$restarted.success -or $restarted.detail.dataRoot -ine $destination) { throw 'The new data location was not retained after restart.' }
$upgraded=$false
$uninstalled=$false
if ($UpgradeSetupPath) {
    if (!$SetupPath) { throw 'Setup is required to verify post-migration installation updates.' }
    $upgradeReport=Join-Path $evidence 'upgrade.json'
    Run-Application $UpgradeSetupPath @('--test-mode','--install','--data-root',$control,'--report',$upgradeReport)
    if (!(Get-Content -LiteralPath $upgradeReport -Raw -Encoding UTF8 | ConvertFrom-Json).success) { throw 'Post-migration app update failed.' }
    $afterReport=Join-Path $evidence 'after-update.json'
    Run-Application $ApplicationPath @('--smoke-test','--output',$afterReport)
    $after=Get-Content -LiteralPath $afterReport -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$after.success -or $after.detail.dataRoot -ine $destination -or (Test-Path -LiteralPath (Join-Path $destination 'app'))) { throw 'App update used the wrong installation or data directory.' }
    $upgraded=$true
}
if ($SetupPath) {
    Run-Application (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $control 'app/Uninstall.ps1'),'-Quiet','-TestMode')
    if ((Test-Path -LiteralPath (Join-Path $control 'app')) -or [IO.File]::ReadAllText((Join-Path $destination $gameRelative)) -ne 'synthetic game data to preserve') { throw 'Uninstall did not preserve relocated game data.' }
    $uninstalled=$true
}
$result=[ordered]@{success=$true; nativeMigration=$true; persistedAfterRestart=$true; originalDataPreserved=$true; credentialsNotCopied=$true; migratedJavaVerified=$migrated.detail.migratedJavaVerified; appUpdateAfterMigration=$upgraded; uninstallAfterMigration=$uninstalled; realGameConnectionTested=$false}
[IO.File]::WriteAllText((Join-Path $evidence 'result.json'),($result | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$result | ConvertTo-Json
