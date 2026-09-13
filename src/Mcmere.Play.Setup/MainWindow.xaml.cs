using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Mcmere.Play.Setup;

public partial class MainWindow : Window
{
    private readonly string[] _args;
    private readonly bool _test;
    private readonly bool _smoke;
    private bool _busy;
    private string? _payload;
    private string? _temporaryPayloadDirectory;
    private InstallationInfo? _installed;
    private string? _updateVersion;
    private string? Option(string name) { var index = Array.IndexOf(_args, name); return index >= 0 && index + 1 < _args.Length ? _args[index + 1] : null; }
    public MainWindow(string[] args)
    {
        _args = args; _test = args.Contains("--test-mode"); _smoke = args.Contains("--smoke-test");
        InitializeComponent();
        RootPath.Text = Option("--data-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcmere-play");
        if (_test || _smoke) { ShowActivated = false; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual; Left = -15000; Top = -15000; }
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
        Closed += (_, _) =>
        {
            if (_temporaryPayloadDirectory is null || _payload is null) return;
            try { File.Delete(_payload); Directory.Delete(_temporaryPayloadDirectory, false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((_test || _smoke) && !Path.GetFullPath(RootPath.Text).Contains(Path.DirectorySeparatorChar + ".test-data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("検証用の保存先には.test-data内のフォルダーを指定してください。");
            var runtime = WebViewAvailable();
            Prerequisite.Text = runtime ? "WebView2 Runtimeを確認しました。" : "WebView2 Runtimeの導入が必要です。";
            if (Option("--update-request") is { } request)
            {
                _busy = true; ChooseButton.IsEnabled = false; InstallButton.IsEnabled = false;
                Status.Text = "アプリの終了を待っています";
                var paths = await PlayPaths.OpenAsync(RootPath.Text, requireIsolated: _test || _smoke);
                var verified = await AppUpdateHandoffVerifier.VerifyAsync(request, paths, Environment.ProcessPath!);
                _updateVersion = verified.Manifest.Version;
                await PlayFiles.WriteAtomicAsync(PlayFiles.Child(paths.ControlRoot, "updates/handoff-status.json"), DistributionJson.Bytes(new { stage = "waiting-parent", version = _updateVersion }));
                await AppUpdateHandoffVerifier.WaitForParentAsync(verified.Handoff);
                await new ProcessActivity(paths).RequireIdleAsync("app-update", default);
                await InstallAsync(); return;
            }
            if (_smoke)
            {
                await ReportAsync(true, new { rendered = true, webView2 = runtime });
                Close(); return;
            }
            if (_args.Contains("--verify-payload"))
            {
                var payload = await PayloadAsync();
                var directory = Path.Combine(RootPath.Text, ".verify", Guid.NewGuid().ToString("N"));
                await SafeArchive.ExtractAsync(payload, directory);
                var package = await InstallationPackage.VerifyAsync(directory);
                await ReportAsync(true, new { package.Version, files = package.Files.Count });
                Close(); return;
            }
            if (_args.Contains("--install")) await InstallAsync();
        }
        catch (Exception error) { _busy = false; Status.Text = error.Message; Details.Text = error.ToString(); await ReportAsync(false, new { error = error.Message }); if (_test || _smoke) Application.Current.Shutdown(1); }
    }
    private async Task<string> PayloadAsync()
    {
        if (_payload is not null) return _payload;
        if (Option("--payload") is { } supplied)
        {
            if (!_test) throw new ArgumentException("外部payloadは検証モードでのみ指定できます。");
            return _payload = Path.GetFullPath(supplied);
        }
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("mcmere-play.payload.zip") ?? throw new InvalidOperationException("セットアップの配布物がありません。");
        var directory = Path.Combine(Path.GetTempPath(), "mcmere-play-setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryPayloadDirectory = directory;
        _payload = Path.Combine(directory, "payload.zip");
        await using (stream)
        await using (var output = File.Create(_payload)) await stream.CopyToAsync(output);
        return _payload;
    }
    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallAsync();
    private async Task InstallAsync()
    {
        _busy = true; InstallButton.IsEnabled = false; ChooseButton.IsEnabled = false; CloseButton.IsEnabled = false; Progress.Visibility = Visibility.Visible;
        try
        {
            if (_updateVersion is not null) await new ProcessActivity(await PlayPaths.OpenAsync(RootPath.Text, requireIsolated: _test || _smoke)).RequireIdleAsync("app-update", default);
            if (!WebViewAvailable())
            {
                if (_test) throw new InvalidOperationException("WebView2 Runtimeがありません。");
                Status.Text = "WebView2 Runtimeを準備しています";
                await InstallWebViewAsync();
            }
            var payload = await PayloadAsync();
            IInstallationRegistration registration = _test ? new TestRegistration() : new WindowsRegistration();
            _installed = await new InstallationEngine(registration).InstallAsync(payload, RootPath.Text, new Progress<string>(text => Status.Text = text), expectedVersion: _updateVersion);
            Status.Text = "インストールが完了しました";
            InstallButton.Visibility = Visibility.Collapsed; OpenButton.Visibility = Visibility.Visible;
            _busy = false; CloseButton.IsEnabled = true; Progress.Visibility = Visibility.Collapsed;
            await ReportAsync(true, new { _installed.Version, _installed.AppDirectory, _installed.DataRoot });
            if (_updateVersion is not null)
            {
                File.Delete(AppUpdater.HandoffPath(new PlayPaths(_installed.DataRoot)));
                if (!_test) { Process.Start(new ProcessStartInfo(Path.Combine(_installed.AppDirectory, "mcmere-play.exe")) { UseShellExecute = true }); Close(); }
            }
            if (_test) { _busy = false; Application.Current.Shutdown(0); }
        }
        catch (Exception error)
        {
            Status.Text = error.Message; Details.Text = error.ToString(); InstallButton.IsEnabled = true;
            await ReportAsync(false, new { error = error.Message });
            if (_test) { _busy = false; Application.Current.Shutdown(1); }
        }
        finally { _busy = false; CloseButton.IsEnabled = true; ChooseButton.IsEnabled = _updateVersion is null; Progress.Visibility = Visibility.Collapsed; }
    }
    private static bool WebViewAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch (WebView2RuntimeNotFoundException) { return false; }
    }
    private async Task InstallWebViewAsync()
    {
        using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
        var path = Path.Combine(Path.GetTempPath(), "mcmere-webview-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            var uri = new Uri("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            var downloaded = false;
            for (var hop = 0; hop < 6; hop++)
            {
                if (uri.Scheme != "https" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
                    !(uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".msedge.net", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("WebView2の配布元を確認できません。");
                using var response = await http.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    uri = response.Headers.Location is { } location ? location.IsAbsoluteUri ? location : new Uri(uri, location) : throw new InvalidOperationException("配布元の転送先がありません。");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(path);
                var buffer = new byte[65536]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer)) > 0)
                {
                    total += count;
                    if (total > 10 * 1024 * 1024) throw new InvalidOperationException("WebView2の配布物が大きすぎます。");
                    await output.WriteAsync(buffer.AsMemory(0, count));
                }
                downloaded = total >= 10000; break;
            }
            if (!downloaded) throw new InvalidOperationException("WebView2の配布物を取得できません。");
            var verifier = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            var command = "$s=Get-AuthenticodeSignature -LiteralPath '" + path.Replace("'", "''", StringComparison.Ordinal) +
                "'; if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch '(^|,\\s*)O=Microsoft Corporation(,|$)'){exit 2}";
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)) }) verifier.ArgumentList.Add(argument);
            using (var verify = Process.Start(verifier)!)
            {
                await verify.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                if (verify.ExitCode != 0) throw new InvalidOperationException("WebView2のMicrosoft署名を確認できません。");
            }
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/silent"); start.ArgumentList.Add("/install");
            using var process = Process.Start(start)!; await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
            if (process.ExitCode != 0 || !WebViewAvailable()) throw new InvalidOperationException("WebView2 Runtimeを導入できませんでした。Microsoftの配布ページから導入してください。");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "mcmere Playを保存する親フォルダーを選択" };
        if (dialog.ShowDialog(this) == true) RootPath.Text = Path.Combine(dialog.FolderName, "mcmere-play");
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_installed is not null) Process.Start(new ProcessStartInfo(Path.Combine(_installed.AppDirectory, "mcmere-play.exe")) { UseShellExecute = true });
        Close();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void License_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/octopusv/mcmere-play/blob/main/LICENSE") { UseShellExecute = true });
    private async Task ReportAsync(bool success, object detail)
    {
        if (Option("--report") is not { } report) return;
        report = Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        await File.WriteAllBytesAsync(report, DistributionJson.Bytes(new { success, detail }));
        UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(this);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.ChangeExtension(report, ".png")); png.Save(file);
    }
    private sealed class TestRegistration : IInstallationRegistration
    {
        public Task ApplyAsync(InstallationInfo installation, CancellationToken ct) => PlayFiles.WriteAtomicAsync(Path.Combine(installation.DataRoot, "registration-test.json"), DistributionJson.Bytes(installation), ct);
        public Task RemoveAsync(InstallationInfo installation, CancellationToken ct) { var path = Path.Combine(installation.DataRoot, "registration-test.json"); if (File.Exists(path)) File.Delete(path); return Task.CompletedTask; }
    }
}
