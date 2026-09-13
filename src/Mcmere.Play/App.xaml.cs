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
            var installation = await InstallationEngine.ReadInstallationAsync(AppContext.BaseDirectory);
            var root = Option("--data-root") ?? installation?.DataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mcmere-play");
            var paths = new PlayPaths(root);
            if (smoke && !paths.Root.Contains(Path.DirectorySeparatorChar + ".test-data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("検証には独立した.test-data内の保存先を指定してください。");
            if (protocol is not null) _ = DistributionTarget.Parse(protocol, development);
            _instance = new SingleInstance(paths);
            if (!_instance.Primary)
            {
                await _instance.SendAsync(protocol ?? "activate");
                Shutdown(0); return;
            }
            var window = new MainWindow(paths, development, smoke, Option("--output"), protocol);
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
