using System.Diagnostics;
using System.Text;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class UninstallDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "play-remove-" + Guid.NewGuid().ToString("N"), ".test-data");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private async Task<(string Control, string Game, string Script)> PrepareAsync()
    {
        var control = Path.Combine(_root, "日本語 Play"); var game = Path.Combine(_root, "ゲーム");
        var app = Path.Combine(control, "app"); Directory.CreateDirectory(app); Directory.CreateDirectory(game);
        var script = Path.Combine(app, "Uninstall.ps1");
        await File.WriteAllTextAsync(script, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Uninstall.ps1")), new UTF8Encoding(true));
        await File.WriteAllBytesAsync(Path.Combine(app, "installation.json"), DistributionJson.Bytes(new InstallationInfo("mcmere-play", "0.1.0", control, app, DateTimeOffset.UtcNow)));
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(DataLocation.Pointer(control), DistributionJson.Bytes(new DataLocationRecord(id, control, game)));
        await File.WriteAllBytesAsync(DataLocation.Receipt(game), DistributionJson.Bytes(new MigrationReceipt(id, control, control, game, 1, 1, DateTimeOffset.UtcNow)));
        foreach (var root in new[] { control, game })
        {
            Directory.CreateDirectory(Path.Combine(root, "prism-data", "instances"));
            await File.WriteAllTextAsync(Path.Combine(root, "prism-data", "instances", "save.txt"), "test-world");
            await File.WriteAllTextAsync(Path.Combine(root, "prism-data", "accounts.json"), "synthetic-credential");
            await File.WriteAllTextAsync(Path.Combine(root, "settings.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(root, "unrelated.txt"), "keep");
        }
        return (control, game, script);
    }
    private static async Task<int> RunAsync(string script, bool remove)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Quiet", "-TestMode" }) start.ArgumentList.Add(arg);
        if (remove) start.ArgumentList.Add("-RemoveData");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(true); throw; }
        await stdout; await stderr; return process.ExitCode;
    }
    [Fact]
    public async Task ExplicitRemovalDeletesOwnedCurrentAndOriginalDataAndKeepsUnrelatedFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await PrepareAsync();
        Assert.Equal(0, await RunAsync(fixture.Script, true));
        Assert.False(Directory.Exists(Path.Combine(fixture.Control, "app")));
        foreach (var root in new[] { fixture.Control, fixture.Game })
        {
            Assert.False(Directory.Exists(Path.Combine(root, "prism-data")));
            Assert.False(File.Exists(Path.Combine(root, "settings.json")));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(root, "unrelated.txt")));
        }
    }
    [Fact]
    public async Task DefaultUninstallRetainsMovedGameData()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await PrepareAsync();
        Assert.Equal(0, await RunAsync(fixture.Script, false));
        Assert.True(File.Exists(Path.Combine(fixture.Game, "prism-data", "accounts.json")));
        Assert.True(File.Exists(Path.Combine(fixture.Control, "settings.json")));
    }
    [Fact]
    public async Task MismatchedMigrationReceiptPreventsAnyDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        var fixture = await PrepareAsync();
        await File.WriteAllBytesAsync(DataLocation.Receipt(fixture.Game), DistributionJson.Bytes(new MigrationReceipt(Guid.NewGuid().ToString("N"), fixture.Control, fixture.Control, fixture.Game, 1, 1, DateTimeOffset.UtcNow)));
        Assert.NotEqual(0, await RunAsync(fixture.Script, true));
        Assert.True(File.Exists(fixture.Script));
        Assert.True(File.Exists(Path.Combine(fixture.Game, "settings.json")));
    }
}
