using Microsoft.Win32;
using Mcmere.Play.Core;
using Mcmere.Play.Setup;

namespace Mcmere.Play.Tests;

public sealed class WindowsRegistrationTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task CurrentUserRegistrationAndShortcutHaveMatchingOwnershipAndVersion()
    {
        if (!OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid().ToString("N");
        var registryRoot = @"Software\mcmere-play\SetupTests\" + id;
        var root = Path.Combine(Path.GetTempPath(), ".test-data", "play-registration-" + id);
        var app = Path.Combine(root, "日本語 folder", "app");
        Directory.CreateDirectory(app);
        var locations = new RegistrationLocations(registryRoot + @"\Uninstall", registryRoot + @"\Protocol", Path.Combine(root, "shortcut.lnk"));
        var registration = new WindowsRegistration(locations);
        var info = new InstallationInfo("mcmere-play", "0.1.0", Path.GetDirectoryName(app)!, app, DateTimeOffset.UtcNow);
        try
        {
            await registration.ApplyAsync(info, default);
            using (var command = Registry.CurrentUser.OpenSubKey(locations.ProtocolKey + @"\shell\open\command"))
                Assert.Equal("\"" + Path.Combine(app, "mcmere-play.exe") + "\" \"%1\"", command!.GetValue(""));
            Assert.True(File.Exists(locations.ShortcutPath));
            await registration.ApplyAsync(info with { Version = "0.2.0" }, default);
            using (var uninstall = Registry.CurrentUser.OpenSubKey(locations.UninstallKey))
            {
                Assert.Equal("0.2.0", uninstall!.GetValue("DisplayVersion"));
                Assert.Equal(app, uninstall.GetValue("InstallLocation"));
                Assert.Contains("\"" + Path.Combine(app, "Uninstall.ps1") + "\"", (string)uninstall.GetValue("UninstallString")!);
            }
            var different = info with { AppDirectory = Path.Combine(root, "another app") };
            await Assert.ThrowsAsync<InvalidOperationException>(() => registration.ApplyAsync(different, default));
            await registration.RemoveAsync(different, default);
            Assert.True(File.Exists(locations.ShortcutPath));
            await registration.RemoveAsync(info, default);
            Assert.False(File.Exists(locations.ShortcutPath));
            Assert.Null(Registry.CurrentUser.OpenSubKey(locations.ProtocolKey));
            Assert.Null(Registry.CurrentUser.OpenSubKey(locations.UninstallKey));
            await registration.RemoveAsync(info, default);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(registryRoot, false);
            Directory.Delete(root, true);
        }
    }
}
