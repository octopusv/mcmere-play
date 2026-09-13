# mcmere Play

**サーバーに合う環境を、準備して参加。**

mcmere Playは、[mcmere](https://github.com/octopusv/mcmere)で管理するMinecraft Javaサーバー向けのWindowsクライアントです。サーバーが公開した構成にJava・NeoForge・MODを揃え、Prism Launcherからゲームを起動するアプリを開発します。

> 現在は設計段階です。このリポジトリには設計と開発・配布方針を収録しています。実行可能なアプリ、Setup EXE、バイナリReleaseはまだありません。

## 予定している使い方

1. 配布ページからmcmere Playをインストールする。
2. サーバーのホワイトリストに登録されたMinecraft名を入力する。
3. 「準備して参加」で不足するJava・MODと設定を用意する。
4. PrismでMinecraftとNeoForgeを準備し、ゲームを起動する。

Play専用のディレクトリとサーバーごとのインスタンスを使い、既存のMinecraft環境・個人設定・セーブを保持します。公開MODは原則として配布元のCDNから、自前の配布物はサーバーの配布APIから取得し、バージョンとファイルハッシュを確認します。

PlayにGoogleログインやメール登録はありません。入力名のホワイトリスト照合は本人認証ではなく、登録名を知っている人も配布を利用できます。実際のゲーム用MicrosoftログインはPrismが担当し、サーバーへの参加はMinecraft側の認証とホワイトリストで判定されます。

## リポジトリの役割

| リポジトリ | 担当 |
|---|---|
| [mcmere](https://github.com/octopusv/mcmere) | サーバー管理、配布候補の作成・検証・公開、名前照合、配布API、管理画面 |
| **mcmere-play** | 参加者向けUI、専用環境、差分同期と復旧、Prism連携、Setup、アプリ更新とRelease |

初版はWindows x64、NeoForgeに対応する計画です。UIはmcmereと共通のデザインを使い、.NET・WPF/WebView2・Reactを採用します。参加者PCにはInfraMere、mcmere管理API、.NET SDK、Node.jsを要求しません。

## 設計と開発

- [設計書](docs/DESIGN.md) — UI、同期、Prism連携、API、データ保護、受け入れ条件
- [SetupとReleaseの方針](docs/RELEASING.md) — 配布物、バージョン、検証、公開手順
- [開発への参加](CONTRIBUTING.md)
- [第三者ライセンス](THIRD_PARTY_NOTICES.md)

最初にPrismとNeoForgeの連携を独立した環境で検証し、同期エンジン、UI、Setupの順で実装します。動作確認の完了後にGitHub Releasesで配布します。

## ライセンス

mcmere Playの独自コードとドキュメントは[MIT License](LICENSE)です。Prism Launcher、Java、Minecraft、NeoForge、MODなどには、それぞれのライセンスと配布条件が適用されます。

Minecraft / Mojang / Microsoft / Prism Launcherの公式製品ではありません。
