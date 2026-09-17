# リソースパックの優先順位

## 原因と修正

対象はMinecraft 1.21.1 / NeoForge 21.1.250。旧ResourcePackOptions.Planは配布パックをoptions.txtのresourcePacks配列の末尾へ追加していたが、初回起動前にはNeoForgeの必須パックID `mod_resources`が配列に存在しなかった。

NeoForgeは選択にないmod_resourcesを`Pack.Position.TOP`へ挿入し、その直後へ子のmodパックを展開する。このため、末尾追加したfileパックもMOD群より低優先になった。先頭insertやZIP内容の差ではなく、Minecraft起動時に暗黙追加される必須パックの扱いが原因。

修正後は、NeoForgeで配布パックを自動有効化するとき、mod_resourcesが未記録ならvanillaの直後（vanillaがなければ先頭）へ補う。配布パックは従来どおり高優先順manifestを反転して末尾へ追加する。既存の非管理パック同士の順序は保持し、既存のmod_resourcesを重複追加しない。配布パックが無効・未選択の場合は基底IDを新規追加しない。

初回の生成例:

```text
resourcePacks:["mod_resources","file/mcmere-example.zip"]
```

vanillaはMinecraft側が必須パックとして低優先側へ補う。既存環境も通常の準備・修復時に再計画されるため、ZIPを再取得しなくても選択順を修復できる。ダウンロード、ZIP配置、SHA512検証、互換性検証、同期journalと復旧処理は変更していない。

## Minecraft / NeoForgeでの意味

実際の21.1.250のclient/universal JARとMinecraft 1.21.1のクラスを調査した。

| 実装 | 確認した動作 |
|---|---|
| `Options.loadSelectedResourcePacks` | 保存配列をLinkedHashSetへ順序を保って渡す |
| `PackRepository.rebuildSelected` | 選択済みパックを展開した後、未選択の必須パックを既定位置へ挿入する |
| `ResourcePackLoader.makePack` | mod_resourcesはrequired=true、TOP、fixedPosition=false。各MODのパックをchildrenとして保持する |
| `ResourcePackLoader.expandAndRemoveRootChildren` | 親の直後へchildrenを追加し、同じhidden childのルート重複を除く |
| `Pack.Position.insert(..., false)` | TOPはリスト末尾側、BOTTOMは先頭側へ挿入する |
| `PackSelectionModel` | 選択画面は内部リストを反転して表示し、確定時に再反転してrepositoryへ渡す |
| `ReloadableResourceManager.createReload` | ログへ出したパックリストをそのままMultiPackResourceManagerへ渡す |
| `FallbackResourceManager.getResource` | 末尾から探索するため、同一リソースは後ろのパックが勝つ |

したがってoptions.txtとReloading ResourceManagerは低優先→高優先、ゲームの選択画面は上が高優先となる。ログの文字列だけを並べ替える修正ではなく、repositoryが構築する選択順を手動選択と揃える修正である。

参照: [NeoForge 1.21.1 Resources](https://docs.neoforged.net/docs/1.21.1/resources/)、[ResourcePackLoader](https://github.com/neoforged/NeoForge/blob/1.21.1/src/main/java/net/neoforged/neoforge/resource/ResourcePackLoader.java)、[PackRepository patch](https://github.com/neoforged/NeoForge/blob/1.21.1/patches/net/minecraft/server/packs/repository/PackRepository.java.patch)。ブランチの最新ソースだけでなく、対象版の実バイナリでも確認した。

## 回帰確認

```powershell
.\scripts\Build.ps1
```

ResourcePackTestsは初回（options未作成・空配列・vanillaのみ）、基底ID欠落の修復、すでに保存された誤順序、既存MOD・個人パックの保持、複数配布パックの優先順位、再適用の冪等性、未選択、ZIPの更新・削除、再取得不要の修復、ハッシュ不一致拒否、中断後の復旧を確認する。DistributionSmokeも、配布パック同士の順序に加えてmod_resourcesより後ろかを確認する。

### 2026-09-18の検証結果

- 修正前に優先順位と修復のテスト6件の失敗を再現した。
- 修正後の標準Build.ps1は全123テスト（ResourcePackTests 17件を含む）、UI、Desktop、Setupが成功した。
- 21.1.250の実PackRepository、ResourcePackLoader、PackSelectionModel、ReloadableResourceManagerを合成パックで実行した。手動側は実際の選択画面のモデルのselect/commit、自動側は修正したPlayのDLLが生成した配列を入力した。実MODの起動やゲーム描画は行っていない。

| ケース | 実Reloading ResourceManagerの順序 | 同一パスのアセットの採用元 |
|---|---|---|
| 旧自動適用 | vanilla → file/mcmere-example.zip → mod_resources → mod/cobblemon → mod/mega_showdown | mod/mega_showdown |
| 手動選択モデル | vanilla → mod_resources → mod/cobblemon → mod/mega_showdown → file/mcmere-example.zip | file/mcmere-example.zip |
| 修正後の自動適用 | 手動選択モデルと完全一致 | file/mcmere-example.zip |

mod/cobblemonとmod/mega_showdownはこの検証では合成パックのID。getResourceとlistResourcesの両方で採用元を検証した。対象ZIPと実MODを使ったポケモンの描画・戦闘、ゲーム本体のlatest.logによる確認は未実施であり、次の受け入れ確認が残る。

## ゲーム内での受け入れ確認

コードとアセット解決の検証だけでは、Cobblemonの戦闘・描画におけるクラッシュ解消を確認したことにはならない。次を隔離した検証用インスタンスと新規ワールドで実施する。既存ワールドやPrismの認証ファイルをfixtureへコピーしない。

1. Minecraft 1.21.1、NeoForge 21.1.250、Cobblemon 1.8.1+1.21.1、Mega Showdown 1.2.0+1.8.1+1.21.1-releaseとATM x MSD v4.0を揃える。
2. 初回自動適用と旧版が生成した選択からの修復の両方を試す。PlayとPrismの通常の準備・起動経路を使い、手動で順序を直さず起動する。
3. 配置ZIPのSHA-256が期待値`999A52C4713582683E64E7045633490A6D121858366068A9C4B0D128BBD518BB`と一致し、latest.logが概ね次の順序になっていることを確認する。

   ```text
   Reloading ResourceManager: vanilla, mod_resources, ..., mod/cobblemon, ..., mod/mega_showdown, ..., file/mcmere-<id>.zip
   ```

4. 同一ZIPをゲームの選択画面で手動適用した場合とも、MOD群に対する実効優先順位を比較する。
5. `/spawnpokemon gengar`で戦闘開始・攻撃・被弾・数十秒の表示、`/spawnpokemon luxray`で戦闘開始・かみくだく等の物理技・被弾・数十秒の表示、`/spawnpokemon luxio`で数十秒の表示を確認する。
6. 次のアニメーション名に対する`not found`とRendering entity in worldのクラッシュがないことを確認する。

   - Gengar: battle_cry / recoil / laugh_quirk
   - Luxray: battle_cry / recoil / physical / blink
   - Luxio: blink

7. Playで再確認・修復して再起動し、順序が維持されることを確認する。配布更新時もZIPのハッシュと選択順を再確認する。

ログの抽出例（対象ゲームフォルダーで実行）:

```powershell
Select-String -LiteralPath logs/latest.log -Pattern 'Reloading ResourceManager:', 'animation\.(gengar|luxray|luxio)\.(battle_cry|recoil|laugh_quirk|physical|blink).*not found', 'Rendering entity in world'
```
