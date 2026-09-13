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
    private readonly PlayApplication _application;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly bool _development;
    private readonly bool _smoke;
    private readonly string? _output;
    private string? _pendingLink;
    private bool _ready;
    private bool _emitting;
    private bool _dirty = true;
    private ActivityState? _lastActivity;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    public MainWindow(PlayPaths paths, bool development, bool smoke, string? output, string? link)
    {
        InitializeComponent();
        _application = new(paths, development); _development = development; _smoke = smoke; _output = output; _pendingLink = link;
        _application.Changed += () => _dirty = true;
        _timer.Tick += async (_, _) => await EmitAsync();
        if (smoke) { ShowActivated = false; ShowInTaskbar = false; Left = -15000; Top = -15000; WindowStartupLocation = WindowStartupLocation.Manual; }
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: PlayFiles.Child(_application.Paths.Root, "webview"));
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
            var view = await _application.ViewAsync(_lifetime.Token);
            if (_dirty || view.Activity != _lastActivity)
            {
                _dirty = false; _lastActivity = view.Activity; Post(new { type = "state", value = view });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { Post(new { type = "error", message = error.Message }); }
        finally { _emitting = false; }
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
            var body = root.TryGetProperty("body", out var data) ? data : default;
            string Text(string key) => body.GetProperty(key).GetString() ?? throw new DistributionException("invalid_request", "入力が不正です。");
            bool Flag(string key, bool fallback = false) => body.ValueKind == JsonValueKind.Object && body.TryGetProperty(key, out var item) ? item.GetBoolean() : fallback;
            object? result = null;
            switch (op)
            {
                case "state": result = await _application.ViewAsync(_lifetime.Token); break;
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
                case "open-releases": External("https://github.com/octopusv/mcmere-play/releases/latest"); break;
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
            var encoded = await Browser.CoreWebView2.ExecuteScriptAsync("JSON.stringify({hasApp:!!document.querySelector('.play-app'),ready:document.querySelector('.play-app')?.dataset.ready==='true',hasNative:!!window.chrome?.webview,hasError:!!document.querySelector('[role=alert]'),text:document.body.innerText.slice(0,1200)})");
            var json = JsonSerializer.Deserialize<string>(encoded) ?? "{}";
            using var result = JsonDocument.Parse(json);
            var state = await _application.ViewAsync(_lifetime.Token);
            await FinishSmokeAsync(result.RootElement.GetProperty("ready").GetBoolean() && !result.RootElement.GetProperty("hasError").GetBoolean() && !state.Activity.Uncertain,
                new { ui = result.RootElement.Clone(), activity = state.Activity });
        }
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
        _ready = false; _timer.Stop(); _lifetime.Cancel(); _application.Dispose(); Browser.Dispose();
    }
}
