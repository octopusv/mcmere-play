using System.Windows;

namespace Mcmere.Play.Setup;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
}
