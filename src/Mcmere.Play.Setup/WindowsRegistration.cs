using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Mcmere.Play.Core;

namespace Mcmere.Play.Setup;

public sealed record RegistrationLocations(string UninstallKey, string ProtocolKey, string ShortcutPath)
{
    public static RegistrationLocations CurrentUser => new(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\mcmere-play",
        @"Software\Classes\mcmere-play", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "mcmere Play.lnk"));
}

[SupportedOSPlatform("windows")]
public sealed class WindowsRegistration(RegistrationLocations? locations = null) : IInstallationRegistration
{
    private readonly RegistrationLocations _locations = locations ?? RegistrationLocations.CurrentUser;
    public Task ApplyAsync(InstallationInfo installation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var exe = Path.Combine(installation.AppDirectory, "mcmere-play.exe");
        using (var existing = Registry.CurrentUser.OpenSubKey(_locations.UninstallKey))
            if (existing?.GetValue("InstallLocation") is string directory && !directory.Equals(installation.AppDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("別の保存先にmcmere Playが登録されています。既存の保存先を指定してください: " + directory);
        using (var existing = Registry.CurrentUser.OpenSubKey(_locations.ProtocolKey + @"\shell\open\command"))
            if (existing?.GetValue("") is string command && !OwnsCommand(command, exe))
                throw new InvalidOperationException("mcmere-playリンクに別のアプリが登録されています。");
        if (File.Exists(_locations.ShortcutPath) && !OwnsShortcut(exe))
            throw new InvalidOperationException("同名の別のショートカットがあります。");
        using (var protocol = Registry.CurrentUser.CreateSubKey(_locations.ProtocolKey))
        {
            protocol.SetValue("", "URL:mcmere Play");
            protocol.SetValue("URL Protocol", "");
            using var command = protocol.CreateSubKey(@"shell\open\command");
            command.SetValue("", Quote(exe) + " \"%1\"");
        }
        using (var uninstall = Registry.CurrentUser.CreateSubKey(_locations.UninstallKey))
        {
            uninstall.SetValue("DisplayName", "mcmere Play");
            uninstall.SetValue("DisplayVersion", installation.Version);
            uninstall.SetValue("Publisher", "mcmere Play contributors");
            uninstall.SetValue("InstallLocation", installation.AppDirectory);
            uninstall.SetValue("DisplayIcon", exe);
            uninstall.SetValue("UninstallString", Quote(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe")) +
                " -NoProfile -ExecutionPolicy Bypass -File " + Quote(Path.Combine(installation.AppDirectory, "Uninstall.ps1")));
            uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        var shortcutPath = _locations.ShortcutPath;
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        WindowsShellLink.Write(shortcutPath, exe, installation.AppDirectory);
        return Task.CompletedTask;
    }
    public Task RemoveAsync(InstallationInfo installation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using (var current = Registry.CurrentUser.OpenSubKey(_locations.UninstallKey))
            if (current?.GetValue("InstallLocation") is string location && location.Equals(installation.AppDirectory, StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(_locations.UninstallKey, false);
        var exe = Path.Combine(installation.AppDirectory, "mcmere-play.exe");
        using (var command = Registry.CurrentUser.OpenSubKey(_locations.ProtocolKey + @"\shell\open\command"))
            if (command?.GetValue("") is string value && OwnsCommand(value, exe))
                Registry.CurrentUser.DeleteSubKeyTree(_locations.ProtocolKey, false);
        if (File.Exists(_locations.ShortcutPath) && OwnsShortcut(exe)) File.Delete(_locations.ShortcutPath);
        return Task.CompletedTask;
    }
    private bool OwnsShortcut(string exe)
    {
        return string.Equals(WindowsShellLink.Target(_locations.ShortcutPath), exe, StringComparison.OrdinalIgnoreCase);
    }
    private static bool OwnsCommand(string command, string exe) => command.Equals(Quote(exe) + " \"%1\"", StringComparison.OrdinalIgnoreCase);
    private static string Quote(string value) => "\"" + value + "\"";
}
