#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PrivateKeyPath,[Parameter(Mandatory)][string]$PublicKeyPath)
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Use Windows to protect the signing key with DPAPI.' }
if ((Test-Path -LiteralPath $PrivateKeyPath) -or (Test-Path -LiteralPath $PublicKeyPath)) { throw 'Existing signing keys must not be overwritten.' }
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    $publicBytes = $rsa.ExportSubjectPublicKeyInfo()
    $keyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($publicBytes)).ToLowerInvariant()
    $privateBytes = $rsa.ExportPkcs8PrivateKey()
    try {
        $secret = ConvertTo-SecureString -String ([Convert]::ToBase64String($privateBytes)) -AsPlainText -Force
        try { $protected = ConvertFrom-SecureString -SecureString $secret } finally { $secret.Dispose() }
    } finally { [Array]::Clear($privateBytes) }
    foreach ($path in @($PrivateKeyPath,$PublicKeyPath)) { New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($path)) -Parent) -Force | Out-Null }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($PrivateKeyPath),$protected,[Text.UTF8Encoding]::new($false))
    $public = [ordered]@{ keyId=$keyId; publicKey=[Convert]::ToBase64String($publicBytes) }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($PublicKeyPath),($public | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ keyId=$keyId; protectedForCurrentWindowsUser=$true; publicKeyPath=[IO.Path]::GetFullPath($PublicKeyPath) } | ConvertTo-Json
} finally { $rsa.Dispose() }
