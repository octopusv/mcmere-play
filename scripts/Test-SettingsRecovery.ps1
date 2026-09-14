#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ApplicationPath,[Parameter(Mandatory)][string]$DataDirectory,[string]$DotnetPath = '')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($DataDirectory)
if ($root -notmatch '[\\/]\.test-data[\\/]' -or (Test-Path -LiteralPath $root)) { throw 'Use a new isolated .test-data directory.' }
New-Item -ItemType Directory -Path $root | Out-Null
$current = Join-Path $root 'settings.json'
[IO.File]::WriteAllText($current,'damaged settings',[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $root 'settings.previous.json'),'{"schemaVersion":1,"theme":"dark","servers":[]}',[Text.UTF8Encoding]::new($false))
$game = Join-Path $root 'prism-data/instances/fixture/.minecraft/saves/keep.txt'
New-Item -ItemType Directory -Path (Split-Path $game -Parent) -Force | Out-Null
[IO.File]::WriteAllText($game,'preserved game data')
$report = Join-Path $root 'recovery-result.json'
$application = [IO.Path]::GetFullPath($ApplicationPath)
$arguments = @('--smoke-test','--smoke-recover-settings','--data-root',$root,'--output',$report)
if ([IO.Path]::GetExtension($application) -ieq '.dll') {
    if (!$DotnetPath) { $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
    $start = [Diagnostics.ProcessStartInfo]::new($DotnetPath)
    $start.ArgumentList.Add($application)
} else { $start = [Diagnostics.ProcessStartInfo]::new($application) }
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
try {
    if (!$process.WaitForExit(60000)) { throw "Recovery test is still running: $($process.Id)" }
    if ($process.ExitCode -ne 0) { throw "Recovery test failed: $report" }
} finally { $process.Dispose() }
$result = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
if (!$result.success -or !$result.detail.settingsRecoveryTested) { throw 'Native settings recovery did not complete.' }
$restored = Get-Content -LiteralPath $current -Raw -Encoding UTF8 | ConvertFrom-Json
if ($restored.theme -ne 'dark' -or [IO.File]::ReadAllText($game) -ne 'preserved game data') { throw 'Recovered preferences or game data do not match.' }
$original = @(Get-ChildItem -LiteralPath $root -File -Filter 'settings.corrupt-*.json')
if ($original.Count -ne 1 -or [IO.File]::ReadAllText($original[0].FullName) -ne 'damaged settings') { throw 'The original settings were not preserved.' }
[ordered]@{ success=$true; nativeUiRecovery=$true; originalPreserved=$true; gameDataPreserved=$true; report=$report; realGameConnectionTested=$false } | ConvertTo-Json
