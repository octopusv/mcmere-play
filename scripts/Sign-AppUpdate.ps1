#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][string]$SetupPath,
    [Parameter(Mandatory)][string]$PrivateKeyPath,[Parameter(Mandatory)][string]$OutputPath,
    [string]$NotesFile='', [switch]$Prerelease, [string]$PublicKeyPath = '')
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'The local release key requires Windows DPAPI.' }
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Use a numeric major.minor.patch version.' }
if (!$PublicKeyPath) { $PublicKeyPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/Mcmere.Play.Core/app-update-key.json' }
$expected = [IO.File]::ReadAllText([IO.Path]::GetFullPath($PublicKeyPath)) | ConvertFrom-Json
$setup = Get-Item -LiteralPath $SetupPath
if ($setup.Name -cne ('mcmere-play-Setup-' + $Version + '.exe')) { throw 'Setup filename and release version do not match.' }
$notes = if ($NotesFile) { [IO.File]::ReadAllText([IO.Path]::GetFullPath($NotesFile)) } else { '' }
if ($notes.Length -gt 8000) { throw 'Release notes are too long.' }
$secure = ConvertTo-SecureString ([IO.File]::ReadAllText([IO.Path]::GetFullPath($PrivateKeyPath)))
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $privateBytes = [Convert]::FromBase64String([Net.NetworkCredential]::new('',$secure).Password)
    try { $consumed=0; $rsa.ImportPkcs8PrivateKey($privateBytes,[ref]$consumed) } finally { [Array]::Clear($privateBytes) }
    $keyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    if ($keyId -cne $expected.keyId -or [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo()) -cne $expected.publicKey) { throw 'Signing key does not match the public key bundled with the app.' }
    $manifest = [ordered]@{ schemaVersion=1; product='mcmere-play'; version=$Version; architecture='win-x64'; prerelease=$Prerelease.IsPresent;
        publishedAt=[DateTimeOffset]::UtcNow.ToString('O'); notes=$notes;
        setupUrl=('https://github.com/octopusv/mcmere-play/releases/download/v'+$Version+'/'+$setup.Name);
        length=$setup.Length; sha256=(Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 5))
    $signature = $rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
    $envelope = [ordered]@{ keyId=$keyId; payload=[Convert]::ToBase64String($bytes);
        sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant(); signature=[Convert]::ToBase64String($signature) }
    New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($OutputPath)) -Parent) -Force | Out-Null
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($envelope | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ version=$Version; manifestSigned=$true; keyId=$keyId; output=[IO.Path]::GetFullPath($OutputPath); authenticodeSigned=$false } | ConvertTo-Json
} finally { $secure.Dispose(); $rsa.Dispose() }
