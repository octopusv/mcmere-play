#requires -Version 7.0
[CmdletBinding()]
param([string]$Version = '0.1.0', [string]$DotnetPath = '', [switch]$SkipBuild, [string]$CertificateThumbprint = '', [switch]$RequireSignature)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must use major.minor.patch.' }
$certificate = $null
if ($CertificateThumbprint) {
    if ($CertificateThumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid certificate thumbprint.' }
    $certificate = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object Thumbprint -eq $CertificateThumbprint
    if (!$certificate -or !$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'A valid code-signing certificate with its private key is required.' }
}
if ($RequireSignature -and !$certificate) { throw 'Supply -CertificateThumbprint for a signed release.' }
function Sign-Executable([string]$Path) {
    if (!$certificate) { return }
    $signature = Set-AuthenticodeSignature -LiteralPath $Path -Certificate $certificate -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
    if ($signature.Status -ne 'Valid') { throw "Code signing failed: $($signature.Status)" }
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
$work = Join-Path $artifacts ('package-' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'app'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Push-Location $projectRoot
try {
    Invoke-Checked @('publish','src/Mcmere.Play/Mcmere.Play.csproj','-c','Release','-r','win-x64','--self-contained','true',('-p:Version='+$Version),'-o',$payload,'--nologo')
    Sign-Executable (Join-Path $payload 'mcmere-play.exe')
    $uninstallSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Uninstall.ps1'))
    [IO.File]::WriteAllText((Join-Path $payload 'Uninstall.ps1'),$uninstallSource,[Text.UTF8Encoding]::new($true))
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
    [pscustomobject]@{ version = $Version; setup = $setupExe; zip = $zipTarget; payload = $payload; signed = [bool]$certificate } | ConvertTo-Json
} finally { Pop-Location }
