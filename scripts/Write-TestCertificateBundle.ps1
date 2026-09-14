#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$SetupPath, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$' -or $CertificateThumbprint -notmatch '^[a-fA-F0-9]{40}$') { throw 'Invalid test bundle identity.' }
$certificate = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $CertificateThumbprint)
if ($certificate.Subject -ne 'CN=mcmere Play Self-Signed Test' -or $certificate.Subject -ne $certificate.Issuer) { throw 'Only the dedicated test certificate may be bundled.' }
$constraints = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' }
if (!$constraints -or $constraints.CertificateAuthority) { throw 'A CA certificate must not be bundled.' }
. (Join-Path $PSScriptRoot 'Verify-TestSignature.ps1')
Assert-TestSignature -Path $SetupPath -Thumbprint $CertificateThumbprint
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$bundle = Join-Path $output ('certificate-bundle-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bundle | Out-Null
$publicCertificate = Join-Path $bundle 'mcmere-play-test.cer'
Export-Certificate -Cert $certificate -FilePath $publicCertificate | Out-Null
$sha = (Get-FileHash -LiteralPath $publicCertificate -Algorithm SHA256).Hash.ToLowerInvariant()
$expires = $certificate.NotAfter.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Test-Certificate.bat.template'))
foreach ($mode in @('install','remove')) {
    $name = if ($mode -eq 'install') { 'Install-Test-Certificate.bat' } else { 'Remove-Test-Certificate.bat' }
    $body = $template.Replace('@THUMBPRINT@',$certificate.Thumbprint).Replace('@SHA256@',$sha).Replace('@MODE@',$mode).Replace('@SUBJECT@',$certificate.Subject).Replace('@EXPIRES@',$expires)
    if ($body -match '@[A-Z0-9]+@') { throw 'Unresolved batch template field.' }
    [IO.File]::WriteAllText((Join-Path $bundle $name),($body -replace '\r?\n',"`r`n"),[Text.ASCIIEncoding]::new())
}
$setupName = 'mcmere-play-Setup-' + $Version + '-self-signed-test.exe'
Copy-Item -LiteralPath $SetupPath -Destination (Join-Path $bundle $setupName)
$readme = @"
# mcmere Play $Version — 個人検証用の自己署名版

信頼された認証局が発行した証明書ではありません。Smart App Controlが有効なPCでの動作は保証しません。通常の配布物と区別して、個人の検証用PCだけで使用してください。

## 使い方

1. ZIPを展開します。
2. Install-Test-Certificate.batを開き、証明書・期限・登録先を確認します。
3. 登録する場合だけ Y を押します。N または中止を選んだ場合は登録しません。
4. 登録後、同じフォルダーの $setupName を実行します。

batは現在のWindowsユーザーの「信頼されたルート証明機関」と「信頼された発行元」へ、この公開証明書だけを登録します。同じ秘密鍵で署名された別のコードも信頼する設定になり得ます。管理者権限への昇格や、全ユーザーへの登録は行いません。Defender・Smart App Control・PowerShellの実行ポリシーは変更しません。

batやSetupがWindowsにブロックされた場合、SACを通過できたとは扱わず、検証を中止してください。

## 証明書

- Subject: $($certificate.Subject)
- Thumbprint: $($certificate.Thumbprint)
- 公開証明書のSHA256: $sha
- 有効期限（UTC）: $expires
- 用途: コード署名のみ。CA証明書ではありません。

登録用batは同梱公開証明書のSHA256を確認し、不一致なら登録しません。秘密鍵はこのZIPに含めていません。

## 登録を戻す

Remove-Test-Certificate.batを開いて Y を押すと、上記Thumbprintの証明書だけを、現在ユーザーの2つの登録先から削除します。アプリとゲームデータは削除しません。

## 確認のみ

Install-Test-Certificate.bat --verify-only は、ファイルのハッシュと証明書情報を表示して終了します。証明書ストアには書き込みません。
"@
[IO.File]::WriteAllText((Join-Path $bundle 'README.txt'),$readme,[Text.UTF8Encoding]::new($false))
$hashes = Get-ChildItem -LiteralPath $bundle -File | Sort-Object Name | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name }
[IO.File]::WriteAllLines((Join-Path $bundle 'SHA256SUMS.txt'),$hashes,[Text.UTF8Encoding]::new($false))
$zip = Join-Path $output ('mcmere-play-' + $Version + '-self-signed-test.zip')
$temporary = Join-Path $output ('bundle-' + [Guid]::NewGuid().ToString('N') + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($bundle,$temporary,[IO.Compression.CompressionLevel]::Optimal,$false)
[IO.File]::Move($temporary,$zip,$true)
[pscustomobject]@{bundle=$bundle;bundleZip=$zip;certificateThumbprint=$certificate.Thumbprint;certificateSha256=$sha;privateKeyIncluded=$false;trustedStoresModified=$false}|ConvertTo-Json
