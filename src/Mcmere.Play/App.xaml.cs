using System.IO;
using System.Windows;
using Mcmere.Play.Core;

namespace Mcmere.Play;

public partial class App : Application
{
    private SingleInstance? _instance;
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            string? Option(string name) { var index = Array.IndexOf(e.Args, name); return index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null; }
            var protocol = e.Args.FirstOrDefault(arg => arg.StartsWith("mcmere-play:", StringComparison.OrdinalIgnoreCase));
            if (protocol is not null && e.Args.Length != 1) throw new ArgumentException("アプリで開くURLに追加の引数は指定できません。");
            var development = e.Args.Contains("--development");
            var smoke = e.Args.Contains("--smoke-test");
            var recoverSettingsSmoke = e.Args.Contains("--smoke-recover-settings");
            if (recoverSettingsSmoke && !smoke) throw new ArgumentException("設定復元の検証には--smoke-testが必要です。");
            var checkUpdateSmoke = e.Args.Contains("--smoke-check-update");
            if (checkUpdateSmoke && !smoke) throw new ArgumentException("更新確認の検証には--smoke-testが必要です。");
            var applyUpdateSmoke = e.Args.Contains("--smoke-apply-update");
            if (applyUpdateSmoke && (!smoke || Option("--output") is null)) throw new ArgumentException("更新適用の検証には--smoke-testと--outputが必要です。");
            var migrateTo = Option("--smoke-migrate-to");
            if (migrateTo is not null && (!smoke || !Path.IsPathFullyQualified(migrateTo) || !PlayPaths.IsIsolated(migrateTo)))
                throw new ArgumentException("移行の検証には--smoke-testと.test-data内の移行先が必要です。");
            var distributionUrl = Option("--smoke-distribution-url");
            var expectedDistributionError = Option("--smoke-distribution-error");
            if (distributionUrl is not null && (!smoke || !development || !Uri.TryCreate(distributionUrl, UriKind.Absolute, out var fixtureUri) ||
                fixtureUri.Scheme != "http" || fixtureUri.Host != "127.0.0.1")) throw new ArgumentException("配布の検証には独立したloopbackの検証サーバーを指定してください。");
            if (expectedDistributionError is not null && (distributionUrl is null || expectedDistributionError is not ("name_not_listed" or "signing_key_changed"))) throw new ArgumentException("配布検証のエラー指定が不正です。");
            var installation = await InstallationEngine.ReadInstallationAsync(AppContext.BaseDirectory);
            var root = Option("--data-root") ?? installation?.DataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcmere-play");
            var paths = await PlayPaths.OpenAsync(root, requireIsolated: smoke);
            if (protocol is not null) _ = DistributionTarget.Parse(protocol, development);
            _instance = new SingleInstance(paths);
            if (!_instance.Primary)
            {
                await _instance.SendAsync(protocol ?? "activate");
                Shutdown(0); return;
            }
            var window = new MainWindow(paths, development, smoke, Option("--output"), protocol, recoverSettingsSmoke, checkUpdateSmoke, applyUpdateSmoke, migrateTo, distributionUrl, expectedDistributionError);
            MainWindow = window;
            _instance.Receive(message => Dispatcher.InvokeAsync(() => window.ReceiveLink(message)));
            window.Show();
        }
        catch (Exception error)
        {
            if (!e.Args.Contains("--smoke-test")) MessageBox.Show(error.Message, "mcmere Play", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e) { _instance?.Dispose(); base.OnExit(e); }
}
