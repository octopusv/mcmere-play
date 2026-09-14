#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BundleDirectory, [Parameter(Mandatory)][string]$TestDirectory,
    [Parameter(Mandatory)][string]$SignedFilePath, [Parameter(Mandatory)][string]$CertificateThumbprint)
$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::GetFullPath($TestDirectory)
if ($testRoot -notmatch '[\\/]\.test-data[\\/]' -or (Test-Path -LiteralPath $testRoot)) { throw 'Choose a new isolated .test-data directory.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
foreach ($name in @('mcmere-play-test.cer','Install-Test-Certificate.bat','Remove-Test-Certificate.bat')) {
    Copy-Item -LiteralPath (Join-Path $BundleDirectory $name) -Destination $testRoot
}
function Trust-State {
    return @(@('Root','TrustedPublisher') | ForEach-Object { Test-Path -LiteralPath ('Cert:\CurrentUser\' + $_ + '\' + $CertificateThumbprint) }) -join ','
}
function Run-Batch([string]$Name, [string]$Argument, [string]$InputText = '') {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $env:SystemRoot 'System32/cmd.exe'))
    $start.WorkingDirectory=$testRoot; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true; $start.RedirectStandardInput=$true
    foreach ($arg in @('/d','/c',$Name)) { $start.ArgumentList.Add($arg) }
    if ($Argument) { $start.ArgumentList.Add($Argument) }
    $process=[Diagnostics.Process]::Start($start)
    $output=$process.StandardOutput.ReadToEndAsync(); $errorOutput=$process.StandardError.ReadToEndAsync()
    if ($InputText) { $process.StandardInput.WriteLine($InputText) }
    $process.StandardInput.Close()
    try {
        if (!$process.WaitForExit(10000)) { $process.Kill($true); throw 'Batch verification timed out.' }
        return [pscustomobject]@{code=$process.ExitCode;output=$output.GetAwaiter().GetResult();error=$errorOutput.GetAwaiter().GetResult()}
    } finally { $process.Dispose() }
}
$before = Trust-State
foreach ($batch in @('Install-Test-Certificate.bat','Remove-Test-Certificate.bat')) {
    $result = Run-Batch $batch '--verify-only'
    if ($result.code -ne 0) { throw ('Batch validation failed: ' + $result.output + $result.error) }
    $cancel = Run-Batch $batch '' 'N'
    if ($cancel.code -ne 2) { throw ('Batch cancellation failed: ' + $cancel.output + $cancel.error) }
}
$certificatePath = Join-Path $testRoot 'mcmere-play-test.cer'
$bytes=[IO.File]::ReadAllBytes($certificatePath); $bytes[30]=$bytes[30] -bxor 1
[IO.File]::WriteAllBytes($certificatePath,$bytes)
if ((Run-Batch 'Install-Test-Certificate.bat' '--verify-only').code -ne 4) { throw 'A tampered certificate was accepted.' }
. (Join-Path $PSScriptRoot 'Verify-TestSignature.ps1')
Assert-TestSignature -Path $SignedFilePath -Thumbprint $CertificateThumbprint
$tampered = Join-Path $testRoot 'tampered.exe'
$bytes=[IO.File]::ReadAllBytes($SignedFilePath); $bytes[0x500]=$bytes[0x500] -bxor 1
[IO.File]::WriteAllBytes($tampered,$bytes)
$rejected=$false
try { Assert-TestSignature -Path $tampered -Thumbprint $CertificateThumbprint } catch { $rejected=$true }
if (!$rejected) { throw 'A tampered executable was accepted.' }
if ((Trust-State) -ne $before) { throw 'Certificate trust changed during read-only/cancellation tests.' }
$report=[ordered]@{success=$true;validCertificateVerified=$true;installCancelled=$true;removalCancelled=$true;tamperedCertificateRejected=$true;tamperedExecutableRejected=$true;trustedStoresUnchanged=$true;actualTrustInstallationTested=$false;smartAppControlAcceptanceTested=$false}
[IO.File]::WriteAllText((Join-Path $testRoot 'result.json'),($report|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$report|ConvertTo-Json
