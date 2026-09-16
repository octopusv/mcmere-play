#requires -Version 7.0
[CmdletBinding()]
param([string]$MetadataPath = '')
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'A Windows user certificate store is required.' }
if (!$MetadataPath) { $MetadataPath = Join-Path (Split-Path $PSScriptRoot -Parent) '.local/self-signed-test.json' }
$MetadataPath = [IO.Path]::GetFullPath($MetadataPath)
if (Test-Path -LiteralPath $MetadataPath) {
    $saved = Get-Content -LiteralPath $MetadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($saved.thumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid stored test certificate identity.' }
    $certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $saved.thumbprint)
    if ($certificate.Subject -ne 'CN=mcmere Play Self-Signed Test' -or !$certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date)) { throw 'The saved test certificate is unavailable or expired.' }
} else {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=mcmere Play Self-Signed Test' -FriendlyName 'mcmere Play Self-Signed Test (90 days)' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -KeyUsage DigitalSignature -NotAfter (Get-Date).AddDays(90) -TextExtension @('2.5.29.19={critical}{text}ca=false')
}
$metadata = [pscustomobject]@{thumbprint=$certificate.Thumbprint;subject=$certificate.Subject;notAfter=$certificate.NotAfter.ToUniversalTime().ToString('O')}
New-Item -ItemType Directory -Path (Split-Path $MetadataPath -Parent) -Force | Out-Null
[IO.File]::WriteAllText($MetadataPath,($metadata|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$metadata
