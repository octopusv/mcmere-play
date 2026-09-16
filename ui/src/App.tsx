import { useEffect, useRef, useState, type ReactNode } from "react";
import { ArrowRight, Check, ChevronDown, Coffee, Download, FolderOpen, History, LoaderCircle, Package, Play, Plus, Puzzle, RotateCw, Settings, UserRound, X } from "lucide-react";
import { isNative, native, subscribe } from "./native";
import { type View, type Discovery, type ServerView, type SavedServer, type MigrationPlan, size, selection } from "./types";

function Dialog({ title, children, close, busy = false }: { title: string; children: ReactNode; close: () => void; busy?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => { ref.current?.showModal(); return () => ref.current?.close(); }, []);
  return <dialog className="play-dialog" ref={ref} aria-label={title} onCancel={event => { if (busy) event.preventDefault(); else close(); }}><header><h2>{title}</h2><button className="icon-button" aria-label="閉じる" disabled={busy} onClick={close}><X size={18} /></button></header>{children}</dialog>;
}
function ErrorText({ message }: { message: string | null | undefined }) { return message ? <div className="play-error" role="alert">{message}</div> : null; }

export default function App() {
  const [view, setView] = useState<View | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [startupErrorCode, setStartupErrorCode] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [tab, setTab] = useState("overview");
  const [dialog, setDialog] = useState<"add" | "name" | "quarantine" | "remove" | "migrate" | null>(null);
  const [migrationPlan, setMigrationPlan] = useState<MigrationPlan | null>(null);
  const [url, setUrl] = useState("");
  const [discovery, setDiscovery] = useState<Discovery | null>(null);
  const [playerName, setPlayerName] = useState("");
  const [requesting, setRequesting] = useState(false);
  const [history, setHistory] = useState<{ id: string; appliedAt: string; operations: { action: string }[] }[]>([]);
  const [applicationSettings, setApplicationSettings] = useState(false);
  const server = view?.settings.servers.find(server => server.id === view.settings.selectedServer) ?? view?.settings.servers[0];
  const state = view?.servers.find(item => item.id === server?.id);
  const busy = requesting || view?.busy;
  const required = server && state?.manifest ? selection(state.manifest.files, server, true) : new Set<string>();
  const selected = server && state?.manifest ? selection(state.manifest.files, server) : new Set<string>();
  useEffect(() => {
    const update = (value: View) => { setView(value); document.documentElement.dataset.theme = value.settings.theme; };
    void native<View>("state").then(update).catch(reason => { setError(reason.message); setStartupErrorCode(reason.code ?? null); });
    return subscribe(message => {
      if (message.type === "state") update(message.value as View);
      if (message.type === "error") { setError(message.message ?? "状態を確認できません。"); setStartupErrorCode(message.code ?? null); }
      if (message.type === "open-server") { setUrl(message.url ?? ""); setDiscovery(null); setError(null); setDialog("add"); }
    });
  }, []);
  useEffect(() => { setPlayerName(server?.playerName ?? ""); setTab("overview"); }, [server?.id]);
  useEffect(() => { if (view) document.documentElement.dataset.theme = view.settings.theme; }, [view?.settings.theme]);
  async function act(op: string, body: unknown = {}) {
    setRequesting(true); setError(null); setNotice(null);
    try { const result = await native(op, body); setView(await native<View>("state")); setError(null); return result; }
    catch (reason) { setError((reason as Error).message); return undefined; }
    finally { setRequesting(false); }
  }
  async function identify() {
    if (!server) return;
    setRequesting(true); setError(null);
    try { await native("identify", { serverId: server.id, name: playerName }); setDialog(null); setView(await native<View>("state")); }
    catch (reason) { setError((reason as Error).message); }
    finally { setRequesting(false); }
  }
  function nameDialog() { setPlayerName(server?.playerName ?? ""); setError(null); setDialog("name"); }
  async function mainAction() {
    if (!server) return;
    if (!server.playerName || ["name_required", "name_not_listed"].includes(state?.errorCode ?? "")) { nameDialog(); return; }
    if (state?.errorCode === "unknown_mods" || (state?.plan?.unknownMods.length ?? 0) > 0) { setDialog("quarantine"); return; }
    if (state?.stage === "launching") { await act("open-prism", { serverId: server.id }); return; }
    if (!state?.manifest || state.stage === "idle" || state.errorCode === "release_changed" || state.status?.compatibilityState !== "matched" && state.stage === "ready") {
      await act("inspect", { serverId: server.id }); return;
    }
    await act("prepare", { serverId: server.id, launch: state.status?.gameState === "online" });
  }
  async function chooseTab(value: string) {
    setTab(value);
    if (value === "history" && server) {
      try { setHistory(await native("history", { serverId: server.id })); } catch (reason) { setError((reason as Error).message); }
    }
  }
  function headline(item: ServerView | undefined, saved: SavedServer | undefined) {
    if (!saved?.playerName) return ["Minecraftの名前を確認しましょう", "名前を入力"];
    if (view?.activity.gameRunning) return ["Minecraftを起動中です", "ゲームを起動中"];
    if (item?.stage === "runtime") return ["JavaとPrismを準備しています", "準備中"];
    if (item?.stage === "checking") return ["プレイ環境を確認しています", "確認中"];
    if (item?.stage === "preparing") return ["MODを準備しています", "準備中"];
    if (item?.stage === "applying") return ["更新を反映しています", "反映中"];
    if (item?.stage === "recovering") return ["前回の更新を復旧しています", "復旧中"];
    if (item?.errorCode === "unknown_mods" || (item?.plan?.unknownMods.length ?? 0) > 0) return ["追加したMODが見つかりました", "退避する内容を確認"];
    if (item?.errorCode === "prism_running") return ["更新する前にPrismを閉じてください", "再試行"];
    if (item?.errorCode === "manual_download") return ["手動で取得するファイルがあります", "再試行"];
    if (item?.status?.gameState === "offline") return ["サーバーは停止中です", "環境を準備"];
    if (item?.stage === "ready" && item.status?.compatibilityState !== "matched") return ["配布内容の確認を待っています", "再確認"];
    if (item?.stage === "prismSetup") return ["Prismで初回起動を完了しましょう", "Prismで起動"];
    if (item?.stage === "ready") return ["参加の準備ができました", "参加する"];
    if (item?.stage === "launching") return ["Prismで起動しています", "Prismを開く"];
    if (item?.stage === "setup") return ["初回セットアップが必要です", "準備して参加"];
    if (item?.stage === "update") return ["プレイ環境を更新します", "準備して参加"];
    if (item?.stage === "error" || item?.stage === "cancelled") return ["環境をもう一度確認してください", "再試行"];
    return ["プレイ環境を確認しましょう", "環境を確認"];
  }
  const [title, button] = headline(state, server);
  const stageDescription = state?.stage === "prismSetup" ? "ゲーム用のログインとMinecraft・NeoForgeの初回取得はPrismで行います。" :
    state?.stage === "launching" ? "ログインやゲームの起動状況はPrismの画面で確認できます。" :
    !server?.playerName ? "このサーバーのホワイトリストに登録されているMinecraft名を入力してください。" :
    "必要なバージョンを確認し、このサーバー専用の環境を準備します。";
  return <div className="app play-app" data-ready={view ? "true" : "false"}>
    <aside className="sidebar">
      <div className="brand"><img className="brand-icon" src="./mcmere-icon-128.png" alt="" /><span>mcmere <em>Play</em></span></div>
      <div className="sidebar-heading">マイサーバー</div>
      <nav className="server-list" aria-label="サーバー選択">{view?.settings.servers.map(item => <button key={item.id} className={"server-item " + (server?.id === item.id && !applicationSettings ? "selected" : "")}
        onClick={() => { void act("select", { serverId: item.id }); setApplicationSettings(false); }}>
        <Puzzle size={19} /><span className="server-label"><strong title={item.name}>{item.name}</strong><small>{item.playerName ?? "名前を入力"}</small></span>
      </button>)}</nav>
      <button className="sidebar-link" onClick={() => { setUrl(""); setDiscovery(null); setDialog("add"); setError(null); }}><Plus size={16} />サーバーを追加</button>
      <div className="sidebar-spacer" />
      <button className="sidebar-link" onClick={() => setApplicationSettings(true)}><Settings size={16} />アプリ設定</button>
      <small className="play-version">mcmere Play {view?.version ?? ""}</small>
    </aside>
    <main className="play-main">
      {!isNative ? <section className="play-empty"><h1>mcmere Playで開いてください</h1><p>この画面はWindowsアプリの中で利用できます。</p></section> :
      !view ? <section className="play-empty">{error ? <><h1>設定を確認してください</h1><ErrorText message={error} />
        {startupErrorCode === "settings_corrupt" && <><p>前回の保存時点の設定に戻します。ゲームデータはそのまま残ります。</p><button className="primary" data-action="recover-settings" disabled={requesting} onClick={() => void act("recover-settings")}>保存済みの設定を復元</button></>}
      </> : <><LoaderCircle className="spin" /><p>読み込み中</p></>}</section> :
      applicationSettings ? <><h1>アプリ設定</h1><div className="play-settings">
        <label>外観<select aria-label="外観" value={view.settings.theme} disabled={busy} onChange={event => void act("theme", { theme: event.target.value })}><option value="system">システム設定</option><option value="light">ライト</option><option value="dark">ダーク</option></select></label>
        <div className="play-settings-row"><div><strong>データ保存先</strong><small data-role="data-root">{view.dataRoot}</small></div><button data-action="choose-migration" disabled={busy || view.update?.queued || ["checking", "downloading", "applying"].includes(view.update?.stage ?? "")} onClick={() => void act("choose-migration").then(value => { if (value) { setMigrationPlan(value as MigrationPlan); setDialog("migrate"); } })}>保存先を変更</button></div>
        {view.migration && <div className="play-update" role="status"><strong>{view.migration.stage === "switching" ? "保存先を切り替えています" : "データをコピーして確認しています"}</strong><p>{view.migration.completedFiles} / {view.migration.totalFiles}ファイル · {size(view.migration.copiedBytes)} / {size(view.migration.totalBytes)}</p>{view.canCancel && <button onClick={() => void native("cancel")}>移行を中止</button>}</div>}
        <div className="play-settings-row"><div><strong>mcmere Play</strong><small>バージョン {view.version}</small></div><button data-action="check-update" disabled={requesting || view.update?.queued} onClick={() => void act("check-update")}>更新を確認</button></div>
        {view.update && <div className="play-update" data-stage={view.update.stage}>
          {view.update.stage === "current" && <p>利用できる更新はありません。</p>}
          {view.update.stage === "unpublished" && <p>配布用の更新情報はまだ公開されていません。</p>}
          {view.update.version && <><strong>バージョン {view.update.version}</strong><p>{view.update.notes || "mcmere Playの更新"}</p></>}
          {view.update.stage === "checking" && <p>更新情報を確認しています。</p>}
          {view.update.stage === "downloading" && <p>更新を取得しています · {size(view.update.received)} / {size(view.update.length)}</p>}
          {view.update.stage === "available" && <button disabled={requesting} onClick={() => void act("download-update")}>更新を準備 · {size(view.update.length)}</button>}
          {view.update.stage === "ready" && !view.update.queued && <button data-action="apply-update" disabled={busy} onClick={() => void act("queue-update", { queued: true })}>{view.activity.gameRunning || view.activity.prismRunning || view.activity.uncertain ? "Prismとゲームの終了後に更新" : "再起動して更新"}</button>}
          {view.update.queued && <><p>Prismとゲームの終了後に更新します。Playは開いたままにしてください。</p><button disabled={requesting} onClick={() => void act("queue-update", { queued: false })}>更新予約を取り消す</button></>}
          <ErrorText message={view.update.error} />
          {view.update.error && <button onClick={() => void act("open-releases")}>リリースを開く</button>}
        </div>}
        <div className="play-settings-row"><div><strong>オープンソース</strong><small>MIT License</small></div><button onClick={() => void act("open-source")}>GitHubを開く</button></div>
        <div className="play-settings-row"><div><strong>診断ログ</strong><small>自動送信は行いません</small></div><button disabled={busy} onClick={() => void act("diagnostics")}>保存する</button></div>
      </div><ErrorText message={error} />{notice && <div className="notice" role="status">{notice}</div>}</> :
      !server ? <section className="play-empty"><h1>最初のサーバーを追加</h1><p>管理者から届いた配布ページを登録して、プレイ環境を準備しましょう。</p>
        <button className="primary" onClick={() => { setDialog("add"); setError(null); }}><Plus size={16} />サーバーを追加</button></section> :
      <><header className="play-heading"><div><h1 title={server.name}>{server.name}</h1><div className="subtitle"><span className={"dot " + (state?.status?.gameState === "online" ? "online" : "")} />
        {state?.status?.gameState === "online" ? "オンライン" : state?.status?.gameState === "offline" ? "停止中" : "状態を確認"}
        {state?.manifest && <span>· 配布版 {state.manifest.displayVersion}</span>}</div></div>
        <button className="play-name" disabled={busy} onClick={nameDialog}><UserRound size={15} />{server.playerName ?? "名前を入力"}<ChevronDown size={14} /></button></header>
        <div className="tabs" role="tablist" aria-label="サーバーの詳細">{[["overview", "概要"], ["mods", "構成"], ["history", "更新履歴"], ["settings", "設定"]].map(([id, label]) =>
          <button role="tab" key={id} aria-selected={tab === id} onClick={() => void chooseTab(id)}>{label}</button>)}</div>
        <ErrorText message={error ?? state?.error} />{notice && <div className="notice" role="status">{notice}</div>}
        {tab === "overview" && <>
          <section className="play-hero" aria-live="polite"><small>{busy ? "プレイ環境を準備しています" : "このサーバーのプレイ環境"}</small><h2>{title}</h2><p>{stageDescription}</p>
            {(state?.total ?? 0) > 0 && <div className="play-progress"><div className="play-progress-track" role="progressbar" aria-label="現在のファイルの取得" aria-valuemin={0} aria-valuemax={state!.total} aria-valuenow={state!.received}><div style={{ width: Math.min(100, state!.received / state!.total * 100) + "%" }} /></div>
              <small>{state?.manifest?.files.find(file => file.id === state.currentFile)?.name ?? (state?.currentFile?.startsWith("prism") ? "Prism Launcher" : state?.currentFile?.startsWith("temurin") ? "Java" : "ファイル")} · {size(state!.received)} / {size(state!.total)}</small></div>}
            <div className="actions"><button className="primary" disabled={busy || view.activity.gameRunning} onClick={() => void mainAction()}>{busy ? <LoaderCircle size={16} className="spin" /> : <Play size={16} />}{button}</button>
              {view.busy && view.canCancel && <button onClick={() => void native("cancel")}>キャンセル</button>}
              {!busy && server.playerName && <button className="text-button" onClick={() => void act("inspect", { serverId: server.id })}><RotateCw size={14} />再確認</button>}</div>
          </section>
          <h2 className="play-section-title">プレイ環境</h2>
          <Environment icon={<Package size={17} />} name="Minecraft" version={state?.manifest?.minecraftVersion} status={server.gameObserved ? "構成を確認" : "初回起動時に取得"} />
          <Environment icon={<Puzzle size={17} />} name="NeoForge" version={state?.manifest?.loader.version} status={server.gameObserved ? "構成を確認" : "初回起動時に取得"} />
          <Environment icon={<Coffee size={17} />} name="Java" version={state?.manifest ? state.manifest.java.major + " · 64bit" : undefined} status={state?.javaReady ? "確認済み" : "未導入"} />
          <Environment icon={<Package size={17} />} name="MOD・パック" version={state?.manifest ? selected.size + "個" : undefined} status={state?.plan ? state.plan.changes.filter(item => item.scope === "game" || item.scope === "resourcePackOptions").length ? "更新が必要" : "確認済み" : "未確認"} />
          <div className="play-footer-note"><FolderOpen size={14} />サーバー専用の環境 · 個人設定とセーブを保持</div>
        </>}
        {tab === "mods" && (state?.manifest ? <><div className="play-section-heading"><h2>このサーバーのMOD・パックと設定</h2><button disabled={busy} onClick={() => void act("choose-reuse").then(value => { if (value) setNotice("選択したフォルダーから、一致するファイルを再利用します。"); })}><FolderOpen size={15} />手元のMODを再利用</button></div>
          {(state.manifest.resourcePacks?.length ?? 0) > 0 && <p>準備時に必要なリソースパックを有効化し、サーバー指定の優先順へ揃えます。画面・音量・操作の設定は保持します。</p>}
          <div className="item-list">{state.manifest.files.map(file => <div className="list-item" key={file.id}><span className="item-symbol"><Package size={20} /></span><div className="item-main"><strong>{file.name}</strong><small>{file.version} · {size(file.length)}</small>
            {file.path.startsWith("resourcepacks/") && <small>リソースパック · 優先順位 {(state.manifest!.resourcePacks?.findIndex(item => item.fileId === file.id) ?? -1) + 1 || "手動"} · {selected.has(file.id) ? "準備時に有効化" : "未選択"}</small>}
            <small>{required.has(file.id) ? "参加に必要" : "推奨"} · {state.plan?.changes.some(item => item.path === file.path) ? "準備が必要" : state.plan ? "確認済み" : "未確認"}</small>
            {file.source.kind === "manual" && <div className="actions"><button onClick={() => void act("manual-page", { serverId: server.id, fileId: file.id })}>配布ページを開く</button><button disabled={busy} onClick={() => void act("import-file", { serverId: server.id, fileId: file.id })}>ファイルを取り込む</button></div>}</div>
            <label className="play-check"><input type="checkbox" aria-label={file.name + "を含める"} checked={selected.has(file.id)} disabled={busy || required.has(file.id)} onChange={event => void act("optional", { serverId: server.id, fileId: file.id, enabled: event.target.checked })} />{required.has(file.id) ? "必須" : "含める"}</label></div>)}</div>
        </> : <p className="play-muted">概要で配布情報を確認してください。</p>)}
        {tab === "history" && <><h2>更新履歴</h2>{state?.manifest && <div className="play-history"><History size={17} /><div><strong>配布版 {state.manifest.displayVersion}</strong><p>{state.manifest.changelog || "サーバーに合わせたプレイ環境"}</p></div></div>}
          {history.map(item => <div className="play-history" key={item.id}><Check size={17} /><div><strong>{new Date(item.appliedAt).toLocaleString("ja-JP")}</strong><p>{item.operations.length}ファイルを反映</p></div></div>)}{history.length === 0 && <p className="play-muted">このPCでの更新履歴はまだありません。</p>}</>}
        {tab === "settings" && <div className="play-settings">
          <div className="play-settings-row"><div><strong>Minecraftの名前</strong><small>{server.playerName ?? "未設定"}</small></div><button disabled={busy} onClick={nameDialog}>変更する</button></div>
          <label>ゲームに割り当てる最大メモリ<select aria-label="ゲームに割り当てる最大メモリ" value={server.memoryMiB} disabled={busy} onChange={event => void act("memory", { serverId: server.id, memoryMiB: Number(event.target.value) })}>{[2048, 4096, 6144, 8192, 12288, 16384].map(value => <option key={value} value={value}>{value / 1024} GiB</option>)}</select></label>
          <div className="play-settings-row"><div><strong>保存先</strong><small>{state?.directory ?? "専用フォルダーに作成します"}</small></div><button disabled={!state?.directory} onClick={() => void act("open-directory", { serverId: server.id })}>フォルダーを開く</button></div>
          <div className="play-settings-row"><div><strong>Prism Launcher</strong><small>ゲーム用アカウントの選択</small></div><button disabled={!state?.prismReady} onClick={() => void act("open-prism", { serverId: server.id })}>Prismを開く</button></div>
          <div className="play-settings-row"><div><strong>環境の修復</strong><small>不足・破損したファイルを確認します</small></div><button disabled={busy} onClick={() => void act("prepare", { serverId: server.id, launch: false })}>確認して修復</button></div>
          <div className="play-settings-row"><div><strong>サーバーの登録</strong><small>登録を外してもゲームデータを保持します</small></div><button disabled={busy} onClick={() => setDialog("remove")}>登録を外す</button></div>
        </div>}
      </>}
    </main>
    {dialog === "migrate" && migrationPlan && <Dialog title="データ保存先を変更" busy={busy} close={() => setDialog(null)}><div className="play-dialog-body"><ErrorText message={error} />
      <p>ゲーム・MOD・Java・設定をコピーし、検証が終わってから切り替えます。アプリ本体の場所は変わりません。</p>
      <div className="play-migration-path"><strong>現在の保存先</strong><p>{migrationPlan.source}</p><strong>新しい保存先</strong><p>{migrationPlan.destination}</p></div>
      <p>{migrationPlan.files}ファイル · {size(migrationPlan.bytes)}をコピー · 必要な空き容量 {size(migrationPlan.requiredFreeBytes)}</p>
      <div className="notice"><p>元のデータは削除しません。</p><p>Prismの全体設定と認証情報は移行しません。移行後はPrismでゲーム用アカウントにログインしてください。</p></div>
      {view?.migration && <p role="status">{view.migration.stage === "switching" ? "保存先を切り替えています" : "コピーと検証を進めています"} · {size(view.migration.copiedBytes)} / {size(view.migration.totalBytes)}</p>}
    </div><footer>{busy ? view?.canCancel && <button onClick={() => void native("cancel")}>移行を中止</button> : <button onClick={() => setDialog(null)}>キャンセル</button>}
      <button className="primary" data-action="confirm-migration" disabled={busy} onClick={() => void act("migrate-data", { planId: migrationPlan.id }).then(value => { if (value) { setDialog(null); setNotice("保存先を変更しました。Prismでゲーム用アカウントにログインしてください。"); } })}>移行して切り替える</button></footer></Dialog>}
    {dialog === "add" && <Dialog title="サーバーを追加" close={() => setDialog(null)}><div className="play-dialog-body"><ErrorText message={error} />
      <label>配布ページのURL<input autoFocus type="url" value={url} onChange={event => { setUrl(event.target.value); setDiscovery(null); }} placeholder="管理者から届いたURL" /></label>
      {discovery && <div className="notice"><strong>{discovery.info.name}</strong><p>{discovery.target.origin}</p></div>}</div><footer><button onClick={() => setDialog(null)}>キャンセル</button>
      <button className="primary" disabled={requesting || !url} onClick={() => void (async () => {
        setRequesting(true); setError(null);
        try { if (!discovery) setDiscovery(await native<Discovery>("discover", { url })); else { await native("add", { url, keyId: discovery.info.signingKey.keyId }); setView(await native<View>("state")); setDialog(null); setApplicationSettings(false); } }
        catch (reason) { setError((reason as Error).message); } finally { setRequesting(false); }
      })()}>{discovery ? "このサーバーを追加" : "サーバーを確認"}<ArrowRight size={15} /></button></footer></Dialog>}
    {dialog === "name" && <Dialog title="Minecraftの名前" close={() => setDialog(null)}><div className="play-dialog-body"><ErrorText message={error} /><p>ホワイトリストに登録されている名前を入力してください。</p>
      <form onSubmit={event => { event.preventDefault(); void identify(); }}><label>Minecraftの名前<input autoFocus value={playerName} maxLength={16} onChange={event => setPlayerName(event.target.value)} autoComplete="off" spellCheck={false} /></label><button type="submit" className="primary" disabled={requesting || !playerName}>名前を確認する</button></form></div></Dialog>}
    {dialog === "quarantine" && server && <Dialog title="追加したMODを退避" close={() => setDialog(null)}><div className="play-dialog-body"><p>次のファイルをバックアップへ退避し、サーバーの標準構成を準備します。</p><ul>{state?.plan?.unknownMods.map(path => <li key={path}>{path}</li>)}</ul></div><footer><button onClick={() => setDialog(null)}>キャンセル</button><button className="primary" disabled={busy} onClick={() => { setDialog(null); void act("prepare", { serverId: server.id, launch: true, quarantine: true }); }}>退避して準備する</button></footer></Dialog>}
    {dialog === "remove" && server && <Dialog title="サーバーの登録を外す" close={() => setDialog(null)}><div className="play-dialog-body"><p>{server.name}を一覧から外します。専用フォルダー内のデータは保持します。</p></div><footer><button onClick={() => setDialog(null)}>キャンセル</button><button className="primary" onClick={() => { setDialog(null); void act("remove", { serverId: server.id }); }}>登録を外す</button></footer></Dialog>}
  </div>;
}

function Environment({ icon, name, version, status }: { icon: ReactNode; name: string; version?: string; status: string }) {
  return <div className="play-environment"><span>{icon}</span><strong>{name}</strong><span className="play-muted">{version ?? "配布情報を確認"}</span><span className={status === "確認済み" ? "play-ok" : "play-muted"}>{status === "確認済み" && <Check size={14} />}{status}</span></div>;
}
