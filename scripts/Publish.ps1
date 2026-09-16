#requires -Version 7.0
[CmdletBinding()]
param([string]$Version = '0.1.2', [string]$DotnetPath = '', [switch]$SkipBuild, [string]$CertificateThumbprint = '', [switch]$RequireSignature, [switch]$SelfSignedTest)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use major.minor.patch.' }
$certificate = $null
if ($CertificateThumbprint) {
    if ($CertificateThumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid certificate thumbprint.' }
    $certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Thumbprint -eq $CertificateThumbprint
    if (!$certificate -or !$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'A valid code-signing certificate with its private key is required.' }
}
if ($RequireSignature -and !$certificate) { throw 'Supply -CertificateThumbprint for a signed release.' }
if ($certificate -and $certificate.Subject -eq $certificate.Issuer -and !$SelfSignedTest) { throw 'Self-signed certificates require -SelfSignedTest and separate test artifacts.' }
if ($SelfSignedTest) {
    if (!$certificate -or $certificate.Subject -ne 'CN=mcmere Play Self-Signed Test' -or $certificate.Subject -ne $certificate.Issuer) { throw 'SelfSignedTest requires the dedicated self-signed test certificate.' }
    $ekuExtension = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
    $eku = @($ekuExtension.EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($eku.Count -ne 1 -or $eku[0] -ne '1.3.6.1.5.5.7.3.3') { throw 'The test certificate must allow code signing only.' }
    $constraints = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' }
    if (!$constraints -or $constraints.CertificateAuthority) { throw 'SelfSignedTest must not use a CA certificate.' }
    . (Join-Path $PSScriptRoot 'Verify-TestSignature.ps1')
}
function Sign-Executable([string]$Path) {
    if (!$certificate) { return }
    if ($SelfSignedTest) {
        $null = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $certificate -HashAlgorithm SHA256
        Assert-TestSignature -Path $Path -Thumbprint $certificate.Thumbprint
    } else {
        $signature = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
        if ($signature.Status -ne 'Valid') { throw "Code signing failed: $($signature.Status)" }
    }
}
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$DotnetPath) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    $DotnetPath = if ($command) { $command.Source } else { Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
}
function Invoke-Checked([string[]]$Arguments) {
    & $DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $LASTEXITCODE" }
}
if (!$SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') -DotnetPath $DotnetPath -SkipDependencyRestore }
$artifacts = Join-Path $projectRoot 'artifacts'
if ($SelfSignedTest) { $artifacts = Join-Path $artifacts 'self-signed-test' }
$work = Join-Path $artifacts ('package-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'app'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Push-Location $projectRoot
try {
    Invoke-Checked @('publish','src/Mcmere.Play/Mcmere.Play.csproj','-c','Release','-r','win-x64','--self-contained','true',('-p:Version='+$Version),'-o',$payload,'--nologo')
    $uninstallSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Uninstall.ps1'))
    [IO.File]::WriteAllText((Join-Path $payload 'Uninstall.ps1'),$uninstallSource,[Text.UTF8Encoding]::new($true))
    foreach ($owned in @('mcmere-play.exe','mcmere-play.dll','Mcmere.Play.Core.dll','Mcmere.Distribution.Contracts.dll','Uninstall.ps1')) { Sign-Executable (Join-Path $payload $owned) }
    Get-ChildItem -LiteralPath $payload -File -Filter '*.pdb' | Remove-Item -Force
    $files = Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = [IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/'); length = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $manifest = [ordered]@{ product = 'mcmere-play'; version = $Version; files = @($files); schemaVersion = 1 }
    [IO.File]::WriteAllText((Join-Path $payload 'payload.json'),($manifest | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $work ('mcmere-play-' + $Version + '-win-x64.zip')
    [IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
    $setup = Join-Path $work 'setup'
    Invoke-Checked @('publish','src/Mcmere.Play.Setup/Mcmere.Play.Setup.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true',('-p:Version='+$Version),('-p:PayloadZip='+$zip),'-o',$setup,'--nologo')
    $setupExe = Join-Path $artifacts ('mcmere-play-Setup-' + $Version + '.exe')
    $zipTarget = Join-Path $artifacts ('mcmere-play-' + $Version + '-win-x64.zip')
    Copy-Item -LiteralPath (Join-Path $setup 'mcmere-play-setup.exe') -Destination $setupExe -Force
    Sign-Executable $setupExe
    Copy-Item -LiteralPath $zip -Destination $zipTarget -Force
    $hashes = @($setupExe,$zipTarget) | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($_) }
    [IO.File]::WriteAllLines((Join-Path $artifacts 'SHA256SUMS.txt'),$hashes,[Text.UTF8Encoding]::new($false))
    if ($SelfSignedTest) {
        & (Join-Path $PSScriptRoot 'Write-TestCertificateBundle.ps1') -Version $Version -CertificateThumbprint $certificate.Thumbprint -SetupPath $setupExe -OutputDirectory $artifacts
    }
    [pscustomobject]@{ version = $Version; setup = $setupExe; zip = $zipTarget; payload = $payload; signed = [bool]$certificate; selfSignedTest = $SelfSignedTest.IsPresent } | ConvertTo-Json
} finally { Pop-Location }
