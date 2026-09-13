using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Mcmere.Play;

public partial class MainWindow : Window
{
    private const string Origin = "https://app.mcmere-play.local";
    private PlayApplication _application;
    private AppUpdater _updates;
    private readonly DateTimeOffset _processStarted;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _development;
    private readonly bool _smoke;
    private readonly bool _recoverSettingsSmoke;
    private readonly bool _checkUpdateSmoke;
    private readonly SmokeUpdateLauncher? _smokeUpdateLauncher;
    private readonly string? _output;
    private readonly string? _smokeMigrationTarget;
    private string? _pendingLink;
    private bool _ready;
    private bool _emitting;
    private bool _dirty = true;
    private bool _migrationBusy;
    private MigrationProgress? _migrationProgress;
    private MigrationPlan? _migrationPlan;
    private int _contextVersion;
    private ActivityState? _lastActivity;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public MainWindow(PlayPaths paths, bool development, bool smoke, string? output, string? link, bool recoverSettingsSmoke = false, bool checkUpdateSmoke = false, bool applyUpdateSmoke = false, string? migrateTo = null)
    {
        InitializeComponent();
        _application = new(paths, development); _development = development; _smoke = smoke; _output = output; _pendingLink = link;
        _smokeUpdateLauncher = applyUpdateSmoke ? new SmokeUpdateLauncher(output + ".setup.json") : null;
        _updates = new(paths, new ProcessActivity(paths), _smokeUpdateLauncher);
        using (var own = Process.GetCurrentProcess()) _processStarted = new DateTimeOffset(own.StartTime.ToUniversalTime());
        _updates.Changed += () => _dirty = true;
        _recoverSettingsSmoke = recoverSettingsSmoke;
        _checkUpdateSmoke = checkUpdateSmoke;
        _smokeMigrationTarget = migrateTo;
        _application.Changed += () => _dirty = true;
        _timer.Tick += async (_, _) => await EmitAsync();
        if (smoke) { ShowActivated = false; ShowInTaskbar = false; Left = -15000; Top = -15000; WindowStartupLocation = WindowStartupLocation.Manual; }
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            try { await _updates.InitializeAsync(_lifetime.Token); }
            catch (DistributionException) { }
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: PlayFiles.Child(_application.Paths.ControlRoot, "webview"));
            await Browser.EnsureCoreWebView2Async(environment);
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping("app.mcmere-play.local", Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.DenyCors);
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = _development;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.NavigationStarting += (_, args) => { if (!OwnOrigin(args.Uri)) args.Cancel = true; };
            Browser.CoreWebView2.NewWindowRequested += (_, args) => { args.Handled = true; if (args.IsUserInitiated) External(args.Uri); };
            Browser.CoreWebView2.WebMessageReceived += OnMessage;
            Browser.CoreWebView2.NavigationCompleted += async (_, args) =>
            {
                if (!args.IsSuccess) return;
                _ready = true; _timer.Start(); await EmitAsync();
                if (_pendingLink is { } url) { _pendingLink = null; Post(new { type = "open-server", url }); }
                if (_smoke) await SmokeAsync();
            };
            Browser.CoreWebView2.Navigate(Origin + "/index.html");
        }
        catch (Exception error)
        {
            if (_smoke) await FinishSmokeAsync(false, new { error = error.Message });
            else MessageBox.Show("画面を起動できません。WebView2 Runtimeを確認してください。\n" + error.Message, "mcmere Play", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    public void ReceiveLink(string value)
    {
        if (value != "activate")
        {
            try { _ = DistributionTarget.Parse(value, _development); }
            catch (DistributionException) { return; }
            if (_ready) Post(new { type = "open-server", url = value }); else _pendingLink = value;
        }
        Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate();
    }
    private static bool OwnOrigin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.GetLeftPart(UriPartial.Authority) == Origin;
    private void Post(object value)
    {
        if (_ready && Browser.CoreWebView2 is not null) Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value, DistributionJson.Options));
    }
    private async Task EmitAsync()
    {
        if (!_ready || _emitting) return;
        _emitting = true;
        try
        {
            var version = _contextVersion;
            var view = await CurrentViewAsync();
            if (version != _contextVersion) return;
            if (_dirty || view.Activity != _lastActivity)
            {
                _dirty = false; _lastActivity = view.Activity; Post(new { type = "state", value = view });
            }
            if (await _updates.TryStartAsync(Environment.ProcessId, _processStarted, view.Busy || _migrationBusy, _lifetime.Token))
            {
                if (_smokeUpdateLauncher is not null) await FinishSmokeAsync(true, new { updateHandoffTested = true, setupProcessId = _smokeUpdateLauncher.ProcessId, setupReport = _smokeUpdateLauncher.Report, appUpdate = _updates.View });
                else Application.Current.Shutdown(0);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Post(new { type = "error", message = error.Message, code = (error as DistributionException)?.Code }); }
        finally { _emitting = false; }
    }
    private async Task<PlayView> CurrentViewAsync() => (await _application.ViewAsync(_lifetime.Token)) with { Update = _updates.View, Migration = _migrationProgress };
    private async Task SwitchDataContextAsync()
    {
        var paths = await PlayPaths.OpenAsync(_application.Paths.ControlRoot, _lifetime.Token, _smoke);
        _application.Dispose(); _updates.Dispose();
        _application = new(paths, _development); _application.Changed += () => _dirty = true;
        _updates = new(paths, new ProcessActivity(paths), _smokeUpdateLauncher); _updates.Changed += () => _dirty = true;
        _contextVersion++; _lastActivity = null; _dirty = true;
        try { await _updates.InitializeAsync(_lifetime.Token); } catch (DistributionException) { }
    }
    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!OwnOrigin(e.Source) || e.WebMessageAsJson.Length > 65536) return;
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            id = root.GetProperty("id").GetString();
            if (!Guid.TryParse(id, out _)) return;
            var op = root.GetProperty("op").GetString();
            if (_migrationBusy && op is not ("state" or "cancel")) throw new DistributionException("migration_busy", "保存先の移行が終わってから操作してください。");
            var body = root.TryGetProperty("body", out var data) ? data : default;
            string Text(string key) => body.GetProperty(key).GetString() ?? throw new DistributionException("invalid_request", "入力が不正です。");
            bool Flag(string key, bool fallback = false) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var item) ? item.GetBoolean() : fallback;
            object? result = null;
            switch (op)
            {
                case "state": result = await CurrentViewAsync(); break;
                case "check-update": await _updates.CheckAsync(_lifetime.Token); break;
                case "download-update": await _updates.DownloadAsync(_lifetime.Token); break;
                case "queue-update": await _updates.QueueAsync(Flag("queued"), _lifetime.Token); break;
                case "choose-migration":
                    if ((await _application.ViewAsync(_lifetime.Token)).Busy || _updates.View.Queued || _updates.View.Stage is "checking" or "downloading" or "applying")
                        throw new DistributionException("operation_busy", "準備やアプリ更新が終わってから保存先を変更してください。");
                    if (_smokeMigrationTarget is not null) result = _migrationPlan = await _application.PlanMigrationAsync(_smokeMigrationTarget, _lifetime.Token);
                    else
                    {
                        var migrationFolder = new OpenFolderDialog { Title = "新しいデータ保存先の親フォルダーを選択", Multiselect = false };
                        if (migrationFolder.ShowDialog(this) == true)
                            result = _migrationPlan = await _application.PlanMigrationAsync(Path.Combine(migrationFolder.FolderName, "mcmere-play-data"), _lifetime.Token);
                    }
                    break;
                case "migrate-data":
                    if (_migrationPlan is null || _migrationPlan.Id != Text("planId")) throw new DistributionException("migration_changed", "移行する保存先をもう一度確認してください。");
                    if (_updates.View.Queued || _updates.View.Stage is "checking" or "downloading" or "applying") throw new DistributionException("update_busy", "アプリ更新が終わってから移行してください。");
                    _migrationBusy = true;
                    try
                    {
                        result = await _application.MigrateDataAsync(_migrationPlan, new Progress<MigrationProgress>(value => { if (_migrationBusy) { _migrationProgress = value; _dirty = true; } }), _lifetime.Token);
                        await SwitchDataContextAsync(); _migrationPlan = null;
                    }
                    finally { _migrationBusy = false; _migrationProgress = null; _dirty = true; }
                    break;
                case "recover-settings": await _application.RestoreSettingsAsync(_lifetime.Token); break;
                case "discover": result = await _application.DiscoverAsync(Text("url"), _lifetime.Token); break;
                case "add": result = await _application.AddAsync(Text("url"), Text("keyId"), _lifetime.Token); break;
                case "select": await _application.SelectAsync(Text("serverId"), _lifetime.Token); break;
                case "identify": await _application.IdentifyAsync(Text("serverId"), Text("name"), _lifetime.Token); break;
                case "inspect": await _application.InspectAsync(Text("serverId"), _lifetime.Token); break;
                case "prepare": await _application.PrepareAsync(Text("serverId"), Flag("launch"), Flag("quarantine"), _lifetime.Token); break;
                case "launch": await _application.LaunchAsync(Text("serverId"), _lifetime.Token); break;
                case "cancel": _application.Cancel(); break;
                case "open-prism": await _application.OpenPrismAsync(Text("serverId"), _lifetime.Token); break;
                case "theme": await _application.SetThemeAsync(Text("theme"), _lifetime.Token); break;
                case "memory": await _application.SetMemoryAsync(Text("serverId"), body.GetProperty("memoryMiB").GetInt32(), _lifetime.Token); break;
                case "optional": await _application.SetOptionalAsync(Text("serverId"), Text("fileId"), Flag("enabled"), _lifetime.Token); break;
                case "remove": await _application.RemoveAsync(Text("serverId"), _lifetime.Token); break;
                case "history": result = await _application.HistoryAsync(Text("serverId"), _lifetime.Token); break;
                case "import-file":
                    var import = new OpenFileDialog { Title = "配布元から取得したファイルを選択", CheckFileExists = true, Multiselect = false };
                    if (import.ShowDialog(this) == true) await _application.ImportFileAsync(Text("serverId"), Text("fileId"), import.FileName, _lifetime.Token);
                    break;
                case "choose-reuse":
                    var folder = new OpenFolderDialog { Title = "再利用するMODのフォルダーを選択", Multiselect = false };
                    if (folder.ShowDialog(this) == true) { _application.AddReuseFolder(folder.FolderName); result = Path.GetFileName(folder.FolderName); }
                    break;
                case "open-directory":
                    var view = await _application.ViewAsync(_lifetime.Token);
                    if (!view.Settings.Servers!.Any(server => server.Id == Text("serverId"))) throw new DistributionException("server_not_found", "サーバーが見つかりません。");
                    var directory = _application.Paths.Game(Text("serverId"));
                    if (!Directory.Exists(directory)) throw new DistributionException("setup_required", "先にプレイ環境を準備してください。");
                    Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
                    break;
                case "diagnostics": result = await DiagnosticsAsync(); break;
                case "open-source": External("https://github.com/octopusv/mcmere-play"); break;
                case "open-releases": External("https://github.com/octopusv/mcmere-play/releases"); break;
                case "manual-page":
                    var current = await _application.ViewAsync(_lifetime.Token);
                    var file = current.Servers.FirstOrDefault(server => server.Id == Text("serverId"))?.Manifest?.Files.FirstOrDefault(file => file.Id == Text("fileId"));
                    if (file?.Source.Kind != FileSourceKind.Manual || file.Source.PageUrl is null) throw new DistributionException("file_not_found", "配布ページが見つかりません。");
                    External(file.Source.PageUrl); break;
                default: throw new DistributionException("unknown_operation", "対応していない操作です。");
            }
            Post(new { id, ok = true, value = result });
            _dirty = true; await EmitAsync();
        }
        catch (Exception error)
        {
            var code = (error as DistributionException)?.Code ?? "operation_failed";
            var message = error is DistributionException ? error.Message : "操作を完了できませんでした。";
            Post(new { id, ok = false, error = new { code, message } });
        }
    }
    private static void External(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
    private async Task<object?> DiagnosticsAsync()
    {
        var dialog = new SaveFileDialog { Title = "診断ログを保存", FileName = "mcmere-play-diagnostics.zip", Filter = "ZIPファイル|*.zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != true) return null;
        var state = await _application.ViewAsync(_lifetime.Token);
        var report = new { version = PlayVersion.Current, operatingSystem = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            servers = state.Servers.Select(server => new { server.Stage, server.ErrorCode, minecraft = server.Manifest?.MinecraftVersion,
                neoForge = server.Manifest?.Loader.Version, java = server.Manifest?.Java.Major,
                changes = server.Plan?.Changes.Select(item => new { item.Scope, item.Path, item.Action }).ToArray() }) };
        var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                await using var entry = zip.CreateEntry("diagnostics.json").Open();
                await entry.WriteAsync(DistributionJson.Bytes(report), _lifetime.Token);
            }
            File.Move(temporary, dialog.FileName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new { saved = true };
    }
    private async Task SmokeAsync()
    {
        try
        {
            await Task.Delay(1200, _lifetime.Token);
            var migratedJavaVerified = false;
            if (_smokeMigrationTarget is not null)
            {
                await Browser.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(button => button.textContent === 'アプリ設定')?.click()");
                async Task Click(string action)
                {
                    for (var attempt = 0; attempt < 100; attempt++)
                    {
                        if (await Browser.CoreWebView2.ExecuteScriptAsync("(() => { const b = document.querySelector('[data-action=\"" + action + "\"]'); if (!b || b.disabled) return false; b.click(); return true; })()") == "true") return;
                        await Task.Delay(100, _lifetime.Token);
                    }
                    throw new InvalidOperationException("移行の操作が表示されませんでした: " + action);
                }
                await Click("choose-migration"); await Click("confirm-migration");
                for (var attempt = 0; attempt < 600; attempt++)
                {
                    if (!_migrationBusy && DataLocation.Same(_application.Paths.Root, _smokeMigrationTarget)) break;
                    await Task.Delay(100, _lifetime.Token);
                }
                if (_migrationBusy || !DataLocation.Same(_application.Paths.Root, _smokeMigrationTarget)) throw new InvalidOperationException("データ保存先の切り替えが完了しませんでした。");
                using var runtimeHttp = VerifiedDownloads.CreateHttpClient();
                var runtimeManager = new RuntimeManager(_application.Paths, new VerifiedDownloads(runtimeHttp, new DownloadPolicy()));
                var java = await runtimeManager.FindAsync(RuntimeCatalog.Java21, "java", _lifetime.Token);
                if (java is not null)
                {
                    await RuntimeManager.InspectJavaAsync(java.Executable, 21, _lifetime.Token);
                    migratedJavaVerified = DataLocation.Within(java.Executable, _smokeMigrationTarget);
                }
                await Task.Delay(500, _lifetime.Token);
            }
            if (_smokeUpdateLauncher is not null)
            {
                await Browser.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(button => button.textContent === 'アプリ設定')?.click()");
                var clicked = false;
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    if (await Browser.CoreWebView2.ExecuteScriptAsync("(() => { const button = document.querySelector('[data-action=\"apply-update\"]'); if (!button || button.disabled) return false; button.click(); return true; })()") == "true") { clicked = true; break; }
                    await Task.Delay(100, _lifetime.Token);
                }
                if (!clicked) throw new InvalidOperationException("更新を適用する操作が表示されませんでした。");
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    if (_updates.View.Queued || _updates.View.Stage == "applying") return;
                    if (_updates.View.Error is not null) throw new InvalidOperationException(_updates.View.Error);
                    await Task.Delay(100, _lifetime.Token);
                }
                throw new InvalidOperationException("更新が予約されませんでした。");
            }
            if (_checkUpdateSmoke)
            {
                await Browser.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('button')].find(button => button.textContent === 'アプリ設定')?.click()");
                var clicked = false;
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    if (await Browser.CoreWebView2.ExecuteScriptAsync("(() => { const button = document.querySelector('[data-action=\"check-update\"]'); if (!button || button.disabled) return false; button.click(); return true; })()") == "true") { clicked = true; break; }
                    await Task.Delay(100, _lifetime.Token);
                }
                if (!clicked) throw new InvalidOperationException("更新確認の操作が表示されませんでした。");
                for (var attempt = 0; attempt < 350; attempt++)
                {
                    if (_updates.View.Stage is "unpublished" or "current" or "available" or "ready" || _updates.View.Error is not null) break;
                    await Task.Delay(100, _lifetime.Token);
                }
                if (_updates.View.Error is not null || _updates.View.Stage is not ("unpublished" or "current" or "available" or "ready"))
                    throw new InvalidOperationException(_updates.View.Error ?? "アプリの更新確認が完了しませんでした。");
                await Task.Delay(500, _lifetime.Token);
            }
            if (_recoverSettingsSmoke)
            {
                var clicked = await Browser.CoreWebView2.ExecuteScriptAsync("(() => { const button = document.querySelector('[data-action=\"recover-settings\"]'); if (!button || button.disabled) return false; button.click(); return true; })()");
                if (clicked != "true") throw new InvalidOperationException("設定復元の操作が表示されませんでした。");
                for (var attempt = 0; attempt < 50; attempt++)
                {
                    if (await Browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.play-app')?.dataset.ready === 'true'") == "true") break;
                    await Task.Delay(100, _lifetime.Token);
                }
            }
            var encoded = await Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({hasApp:!!document.querySelector('.play-app'),ready:document.querySelector('.play-app')?.dataset.ready==='true',hasNative:!!window.chrome?.webview,hasError:!!document.querySelector('[role=alert]'),text:document.body.innerText.slice(0,1200)})");
            var json = JsonSerializer.Deserialize<string>(encoded) ?? "{}";
            using var result = JsonDocument.Parse(json);
            var state = await _application.ViewAsync(_lifetime.Token);
            await FinishSmokeAsync(result.RootElement.GetProperty("ready").GetBoolean() && !result.RootElement.GetProperty("hasError").GetBoolean() && !state.Activity.Uncertain,
                new { ui = result.RootElement.Clone(), activity = state.Activity, settingsRecoveryTested = _recoverSettingsSmoke, updateCheckTested = _checkUpdateSmoke, appUpdate = _updates.View,
                    dataRoot = _application.Paths.Root, controlRoot = _application.Paths.ControlRoot, migrationTested = _smokeMigrationTarget is not null, migratedJavaVerified });
        }
        catch (OperationCanceledException) when (_smokeUpdateLauncher is not null && _updates.View.Stage == "applying") { }
        catch (Exception error) { await FinishSmokeAsync(false, new { error = error.Message }); }
    }
    private async Task FinishSmokeAsync(bool success, object detail)
    {
        if (_output is not null)
        {
            var path = Path.GetFullPath(_output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, DistributionJson.Bytes(new { success, detail }));
            if (_ready)
            {
                await using var screenshot = File.Create(Path.ChangeExtension(path, ".png"));
                await Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot);
            }
        }
        Application.Current.Shutdown(success ? 0 : 1);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _ready = false; _timer.Stop(); _lifetime.Cancel(); _application.Dispose(); _updates.Dispose(); Browser.Dispose();
    }
    private sealed class SmokeUpdateLauncher(string report) : IAppUpdateLauncher
    {
        public string Report { get; } = report;
        public int ProcessId { get; private set; }
        public void Start(ProcessStartInfo start)
        {
            foreach (var value in new[] { "--test-mode", "--report", Report }) start.ArgumentList.Add(value);
            start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("更新用セットアップを起動できません。");
            ProcessId = process.Id;
        }
    }
}
