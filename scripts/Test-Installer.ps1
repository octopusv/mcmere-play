#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$SetupPath,[Parameter(Mandatory)][string]$DataDirectory,[string]$UpgradeSetupPath = '')
$ErrorActionPreference = 'Stop'
$SetupPath = [IO.Path]::GetFullPath($SetupPath)
$testRoot = [IO.Path]::GetFullPath($DataDirectory)
if ($testRoot -notmatch '[\\/]\.test-data[\\/]') { throw 'Use an isolated .test-data directory.' }
if (Test-Path -LiteralPath $testRoot) { throw 'Use a new directory for installer acceptance.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
$evidence = Join-Path $testRoot 'evidence'
New-Item -ItemType Directory -Path $evidence | Out-Null
function Run-Exe([string]$Executable,[string[]]$Arguments,[string]$Report) {
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(120000)) { throw "Process is still running: $($process.Id)" }
        if ($process.ExitCode -ne 0) { throw "Process failed: $($process.ExitCode); report: $Report" }
    } finally { $process.Dispose() }
    $result = Get-Content -LiteralPath $Report -Raw | ConvertFrom-Json
    if (!$result.success) { throw "Verification failed: $Report" }
    return $result
}
$verifyRoot = Join-Path $testRoot 'verify'
$verifyReport = Join-Path $evidence 'payload.json'
$verified = Run-Exe $SetupPath @('--verify-payload','--test-mode','--data-root',$verifyRoot,'--report',$verifyReport) $verifyReport
$installRoot = Join-Path $testRoot '日本語 installed play'
$installReport = Join-Path $evidence 'install.json'
$installed = Run-Exe $SetupPath @('--install','--test-mode','--data-root',$installRoot,'--report',$installReport) $installReport
if ($installed.detail.version -ne $verified.detail.version -or $installed.detail.dataRoot -ine $installRoot) { throw 'Installed version or data root does not match the payload.' }
$settings = '{"schemaVersion":1,"theme":"dark","selectedServer":null,"servers":[]}'
[IO.File]::WriteAllText((Join-Path $installRoot 'settings.json'),$settings,[Text.UTF8Encoding]::new($false))
$world = Join-Path $installRoot 'prism-data/instances/fixture/.minecraft/saves/keep.txt'
New-Item -ItemType Directory -Path (Split-Path $world -Parent) -Force | Out-Null
[IO.File]::WriteAllText($world,'preserve this game data')
$application = Join-Path $installRoot 'app/mcmere-play.exe'
$launchReport = Join-Path $evidence 'launch.json'
$launched = Run-Exe $application @('--smoke-test','--output',$launchReport) $launchReport
if (!$launched.detail.ui.text.Contains('mcmere Play ' + $installed.detail.version)) { throw 'Application displayed an incorrect version.' }
$updateReport = Join-Path $evidence 'update.json'
[void](Run-Exe $SetupPath @('--install','--test-mode','--data-root',$installRoot,'--report',$updateReport) $updateReport)
$upgraded = $null
if ($UpgradeSetupPath) {
    $upgradeReport = Join-Path $evidence 'upgrade.json'
    $upgraded = Run-Exe ([IO.Path]::GetFullPath($UpgradeSetupPath)) @('--install','--test-mode','--data-root',$installRoot,'--report',$upgradeReport) $upgradeReport
    if ([version]$upgraded.detail.version -le [version]$installed.detail.version) { throw 'Upgrade did not install a newer version.' }
    $upgradeLaunchReport = Join-Path $evidence 'upgrade-launch.json'
    $upgradeLaunched = Run-Exe $application @('--smoke-test','--output',$upgradeLaunchReport) $upgradeLaunchReport
    if (!$upgradeLaunched.detail.ui.text.Contains('mcmere Play ' + $upgraded.detail.version)) { throw 'Upgraded application displayed an incorrect version.' }
}
if ([IO.File]::ReadAllText((Join-Path $installRoot 'settings.json')) -ne $settings -or [IO.File]::ReadAllText($world) -ne 'preserve this game data') { throw 'Update modified user data.' }
$powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
& $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installRoot 'app/Uninstall.ps1') -Quiet -TestMode
if ($LASTEXITCODE -ne 0) { throw 'Uninstall failed.' }
if (Test-Path -LiteralPath (Join-Path $installRoot 'app')) { throw 'App directory remained after uninstall.' }
if ([IO.File]::ReadAllText($world) -ne 'preserve this game data' -or [IO.File]::ReadAllText((Join-Path $installRoot 'settings.json')) -ne $settings) { throw 'Uninstall modified user data.' }
$result = [ordered]@{ success=$true; setup=$SetupPath; setupSha256=(Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant(); isolated=$true; payloadVerified=$true; installed=$true; appLaunched=$true; reinstalled=$true; initialVersion=$installed.detail.version; upgradedVersion=if ($upgraded) { $upgraded.detail.version } else { $null }; uninstalled=$true; dataPreserved=$true; windowsRegistrationTested=$false; realGameConnectionTested=$false }
[IO.File]::WriteAllText((Join-Path $evidence 'result.json'),($result | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$result | ConvertTo-Json
