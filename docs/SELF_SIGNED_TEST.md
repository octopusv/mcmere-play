# 個人検証用の自己署名版

個人の検証用PCで使うための配布形式です。第三者の認証局による本人確認を受けた証明書ではなく、Smart App Controlが有効なPCでの実行は保証しません。

## 使う場合

Releaseに添付された`mcmere-play-<version>-self-signed-test.zip`を展開します。同梱の`README.txt`を読み、`Install-Test-Certificate.bat`で証明書・有効期限・登録範囲を確認してください。登録する場合だけ`Y`を押し、その後に同梱の自己署名版Setupを実行します。

登録先は、現在のWindowsユーザーの「信頼されたルート証明機関」と「信頼された発行元」です。対象証明書はコード署名用途の非CA証明書ですが、同じ秘密鍵で署名された別のコードも信頼する設定になり得ます。管理者権限へ昇格せず、全ユーザーへの登録やDefender・SAC・PowerShell実行ポリシーの変更は行いません。

登録を戻すときは`Remove-Test-Certificate.bat`を開いて`Y`を押します。同梱証明書のThumbprintと一致するものだけを現在ユーザーの2つのストアから削除します。アプリ・ゲームデータは削除しません。

batやSetupがWindowsにブロックされた場合は検証を中止してください。この手順をSAC対応済みの手順として案内しないでください。

## 作成する場合

WindowsとPowerShell 7、通常のビルド環境を使用します。

```powershell
$certificate = .\scripts\Initialize-TestCertificate.ps1
.\scripts\Publish.ps1 -Version 0.1.0 -CertificateThumbprint $certificate.thumbprint -SelfSignedTest
```

証明書はRSA 3072bit・コード署名専用・有効期限90日で生成します。秘密鍵はエクスポート不可として現在ユーザーの個人ストアに保存し、公開証明書だけをZIPへ含めます。証明書の作成・ビルド時に信頼ストアへ登録することはありません。期限切れ後は新しい検証用証明書が必要です。

成果物は`artifacts/self-signed-test/`へ出力します。通常ReleaseのSetup・ZIP・署名済み更新情報とは別に扱い、通常のアプリ更新情報でこの試験用Setupを指示しないでください。

独自のEXE・DLL・アンインストールスクリプトを署名してからpayloadのハッシュを作り、最後にSetupを署名します。第三者のバイナリは再署名しません。署名検証は証明書の一致とWindowsのWinVerifyTrustで行い、試験用証明書に限り「信頼されないルート」の結果を許容します。改ざんや他の署名エラーは拒否します。

batは証明書ファイルのSHA256を照合してから、利用者の確認を待ちます。確認だけの場合は`Install-Test-Certificate.bat --verify-only`を使用できます。

`scripts/Test-CertificateBundle.ps1`は、隔離した`.test-data`でハッシュ照合・登録と削除のキャンセル・改ざん拒否を検証します。実際の信頼登録やSACでの許可を検証するスクリプトではありません。
