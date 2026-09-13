# SetupとReleaseの方針

SetupとZIPはローカルで生成できる。CIのRelease workflow、署名済みアプリ更新情報、公開バイナリReleaseと実ゲームでの最終確認は準備中。

## 独立したアプリとして配布する

mcmere Playはmcmere本体とは別のバージョン、Setup、GitHub Releaseを持つ。mcmereのインストーラーやInfraMereの更新を参加者PCへ含めない。mcmere側の配布APIとはschema versionと互換性テストで対応を確認する。

| 配布物 | 用途 |
|---|---|
| `mcmere-play-Setup-<version>.exe` | 通常の利用者向けユーザー単位セットアップ |
| `mcmere-play-<version>-win-x64.zip` | 展開して使える同一アプリのZIP。データは通常版と同じ専用領域に保存 |
| `SHA256SUMS.txt` | リリース資材のファイル整合性確認 |
| 署名済み更新manifest | アプリ自身の更新先・互換性・ハッシュ |

アプリ名は「mcmere Play」、GitHubリポジトリは`mcmere-play`、tagは`v<major>.<minor>.<patch>`。初期の検証版はPrereleaseとして公開し、日常利用に必要な検証を完了してから安定版にする。実装前の空のReleaseや動作しないSetupは公開しない。

## Setupの責任

- Windows x64用の自己完結した.NETアプリをユーザー領域へ配置する。
- WebView2の有無を確認し、不足時はMicrosoftの正規配布から導入・案内する。
- スタートメニュー、アンインストール、ユーザー単位の`mcmere-play://` handlerを登録する。
- 既存インストールではアプリのみを更新し、ゲームデータと個人設定を保持する。
- Prism・Javaは必要な検証済み配布物を後から専用領域へ取得する。Minecraft本体・MOD・アカウントはSetupに埋め込まない。
- アプリのpayloadと更新情報を検証する。コード署名は別途用意する証明書で行い、署名済みと未署名を取り違えない。
- アプリ本体・ランタイム・インスタンス・キャッシュ・バックアップの所有範囲を明示する。

## Releaseの作成順

ローカルの配布物はPowerShell 7から生成する。生成結果はartifactsに保存し、Gitへコミットしない。

```powershell
.\scripts\Publish.ps1 -Version 0.1.0
.\scripts\Test-Installer.ps1 -SetupPath .\artifacts\mcmere-play-Setup-0.1.0.exe -DataDirectory .\.test-data\installer-acceptance
```

受け入れスクリプトは新しい.test-data内にだけインストールし、埋め込みpayload検証、日本語と空白を含む保存先、インストール情報からの保存先解決、実際のアプリ起動、同じ版の再導入、アンインストール、設定・ゲームデータ保持を確認する。`-UpgradeSetupPath`に上位版のSetupを指定すると、バージョンをまたぐ更新と更新後のアプリ表示も確認する。各工程のJSONとPNGを保存する。Windows登録は検証用の記録に置き換えるため、このスクリプト単体では実際のprotocol登録を証明しない。WindowsRegistrationTestsは別の一時HKCUキーとショートカットで本番と同じ登録コードを検証する。

セットアップはユーザー単位で動作し、起動中のPlayを上書きしない。更新中のjournalからアプリと登録情報を回復し、破損payloadでは切り替えない。アンインストールは専用領域のappだけを削除する。通常の配布元からPrismやJavaを取得する処理と、MicrosoftのWebView2不足時の導入処理は別である。WebView2の導入分岐は、RuntimeがないWindows環境での追加検証が必要。

現行のPublish.ps1が生成するSetupはコード署名されていない。SHA256SUMSとpayload検証はファイル整合性の確認であり、配布者のコード署名の代わりではない。

設定復旧のネイティブ検証には以下を使う。起動画面の復元ボタンから通常のWebView2 bridgeを通して操作し、以前の設定と元の破損ファイル、ゲームデータが保持されたことを検証する。DLLを指定する場合は.NET Runtimeを使用する。自己完結した配布EXEもApplicationPathに指定できる。

```powershell
.\scripts\Test-SettingsRecovery.ps1 -ApplicationPath .\src\Mcmere.Play\bin\Release\net8.0-windows\mcmere-play.dll -DataDirectory .\.test-data\settings-recovery
```

1. リリースするcommitとバージョンを確定する。
2. UI、.NET、同期エンジン、Prism連携、インストーラーの検証を実行する。
3. 配布する同一payloadからEXEとZIPを生成し、ハッシュと署名を確認する。
4. 初回インストール、既存版からの更新、失敗・回復、アンインストールを独立した環境で検証する。
5. クリーンな別PCでMinecraft名の照合からPrismログイン、NeoForge起動、対象サーバー接続まで確認する。
6. 対象commitへtagを作り、GitHubにDraft Releaseを作成して検証済み資材を添付する。
7. Release本文に変更点、対応環境、必要な初回操作、既知の制限を記載し、資材を再確認して公開する。

最初のバイナリReleaseには、通信断・容量不足・同時起動・更新中の強制終了・ホワイトリスト削除・既存設定保持の試験も必要。配布APIと公開パックの準備ができていることも確認する。

将来のGitHub Actionsは、PR時にbuild/test、保護されたrelease操作でpackage/sign/uploadを実行する。バージョンとpayloadの不一致や、未検証バイナリの公開を防ぐ。

## リポジトリとReleaseに含めないもの

プレイヤー情報、ホワイトリスト、サーバーIDと個別接続先、認証token、Prismアカウントファイル、署名秘密鍵、実運用の構成・ログ、ワールド、取得したMinecraft/MODのキャッシュはコミットしない。実運用に依存する値は設定として注入し、説明やテストには合成データを使う。
