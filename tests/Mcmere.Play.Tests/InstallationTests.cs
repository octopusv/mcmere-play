using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class InstallationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "play-install-" + Guid.NewGuid().ToString("N"));
    private readonly Registration _registration = new();
    public InstallationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string Data => Path.Combine(_root, "data");
    private async Task<string> Package(string version, bool corrupt = false)
    {
        var zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        var files = new List<PackageFile>();
        foreach (var name in InstallationPackage.RequiredFiles)
        {
            var bytes = Encoding.UTF8.GetBytes(name + " " + version);
            files.Add(new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
            await using var stream = archive.CreateEntry(name).Open();
            await stream.WriteAsync(corrupt && name == "mcmere-play.exe" ? Encoding.UTF8.GetBytes("corrupt") : bytes);
        }
        await using var manifest = archive.CreateEntry("payload.json").Open();
        await manifest.WriteAsync(DistributionJson.Bytes(new ApplicationPackage("mcmere-play", version, files)));
        return zip;
    }
    [Fact]
    public async Task InstallAndUpgradePreserveAllGameData()
    {
        var engine = new InstallationEngine(_registration);
        var installed = await engine.InstallAsync(await Package("0.1.0"), Data);
        Directory.CreateDirectory(Path.Combine(Data, "prism-data"));
        await File.WriteAllTextAsync(Path.Combine(Data, "settings.json"), "personal settings");
        await File.WriteAllTextAsync(Path.Combine(Data, "prism-data", "personal.dat"), "game data");
        var updated = await engine.InstallAsync(await Package("0.2.0"), Data);
        Assert.Equal("0.2.0", updated.Version);
        Assert.Equal("personal settings", await File.ReadAllTextAsync(Path.Combine(Data, "settings.json")));
        Assert.Equal("game data", await File.ReadAllTextAsync(Path.Combine(Data, "prism-data", "personal.dat")));
        Assert.Equal(updated, await InstallationEngine.ReadInstallationAsync(updated.AppDirectory + Path.DirectorySeparatorChar));
        Assert.Equal("0.2.0", _registration.Current!.Version);
    }
    [Fact]
    public async Task CorruptPackageNeverReplacesAnExistingApp()
    {
        var engine = new InstallationEngine(_registration);
        await engine.InstallAsync(await Package("0.1.0"), Data);
        var corrupt = await Package("0.2.0", true);
        await Assert.ThrowsAsync<DistributionException>(() => engine.InstallAsync(corrupt, Data));
        Assert.Equal("0.1.0", (await InstallationEngine.ReadInstallationAsync(Path.Combine(Data, "app")))!.Version);
    }
    [Fact]
    public async Task RegistrationFailureRestoresThePreviousApplication()
    {
        var engine = new InstallationEngine(_registration);
        await engine.InstallAsync(await Package("0.1.0"), Data);
        _registration.FailVersion = "0.2.0";
        var update = await Package("0.2.0");
        await Assert.ThrowsAsync<IOException>(() => engine.InstallAsync(update, Data));
        Assert.Equal("0.1.0", (await InstallationEngine.ReadInstallationAsync(Path.Combine(Data, "app")))!.Version);
        Assert.Equal("0.1.0", _registration.Current!.Version);
    }
    [Fact]
    public async Task RunningApplicationAndUnownedAppDirectoryAreNotOverwritten()
    {
        Directory.CreateDirectory(Data);
        var package = await Package("0.1.0");
        await using (var app = new FileStream(Path.Combine(Data, "application.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal("application_running", (await Assert.ThrowsAsync<DistributionException>(() => new InstallationEngine(_registration).InstallAsync(package, Data))).Code);
        Directory.CreateDirectory(Path.Combine(Data, "app"));
        await File.WriteAllTextAsync(Path.Combine(Data, "app", "unrelated.txt"), "keep");
        Assert.Equal("unowned_destination", (await Assert.ThrowsAsync<DistributionException>(() => new InstallationEngine(_registration).InstallAsync(package, Data))).Code);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(Data, "app", "unrelated.txt")));
    }
    [Fact]
    public async Task InsufficientSpaceIsDetectedBeforeExtraction()
    {
        var package = await Package("0.1.0");
        Assert.Equal("disk_space", (await Assert.ThrowsAsync<DistributionException>(() => new InstallationEngine(_registration, new NoSpace()).InstallAsync(package, Data))).Code);
        Assert.False(Directory.Exists(Path.Combine(Data, ".setup")));
        Assert.Null(_registration.Current);
    }
    private sealed class NoSpace : IAvailableSpace { public long Bytes(string directory) => 0; }
    [Theory]
    [InlineData("prepared", false)]
    [InlineData("prepared", true)]
    [InlineData("switched", true)]
    [InlineData("committed", true)]
    public async Task InterruptedUpgradeRestoresMatchingFilesAndRegistrationBeforeReadingNewPayload(string phase, bool switched)
    {
        var engine = new InstallationEngine(_registration);
        var previous = await engine.InstallAsync(await Package("0.1.0"), Data);
        var next = previous with { Version = "0.2.0", InstalledAt = previous.InstalledAt.AddMinutes(1) };
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(Data, ".setup", id);
        ZipFile.ExtractToDirectory(await Package("0.2.0"), staging);
        await File.WriteAllBytesAsync(Path.Combine(staging, "installation.json"), DistributionJson.Bytes(next));
        if (switched)
        {
            Directory.Move(previous.AppDirectory, Path.Combine(Data, ".setup", "previous-" + id));
            Directory.Move(staging, previous.AppDirectory);
            await _registration.ApplyAsync(next, default);
        }
        await File.WriteAllBytesAsync(Path.Combine(Data, ".setup-journal.json"),
            DistributionJson.Bytes(new { id, hadPrevious = true, phase, installation = next }));
        await Assert.ThrowsAsync<FileNotFoundException>(() => new InstallationEngine(_registration).InstallAsync(Path.Combine(_root, "missing.zip"), Data));
        var expected = phase == "committed" ? next : previous;
        Assert.Equal(expected, await InstallationEngine.ReadInstallationAsync(previous.AppDirectory));
        Assert.Equal(expected, _registration.Current);
        Assert.False(File.Exists(Path.Combine(Data, ".setup-journal.json")));
    }
    [Fact]
    public async Task InterruptedFirstInstallRemovesItsRegistrationAndCanRetryFailedRecovery()
    {
        var engine = new InstallationEngine(_registration);
        var next = await engine.InstallAsync(await Package("0.1.0"), Data);
        var id = Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(Path.Combine(Data, ".setup-journal.json"),
            DistributionJson.Bytes(new { id, hadPrevious = false, phase = "switched", installation = next }));
        _registration.FailRemove = true;
        await Assert.ThrowsAsync<IOException>(() => engine.InstallAsync(Path.Combine(_root, "missing.zip"), Data));
        Assert.True(File.Exists(Path.Combine(Data, ".setup-journal.json")));
        Assert.False(Directory.Exists(next.AppDirectory));
        _registration.FailRemove = false;
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.InstallAsync(Path.Combine(_root, "missing.zip"), Data));
        Assert.Null(_registration.Current);
        Assert.False(File.Exists(Path.Combine(Data, ".setup-journal.json")));
        var retry = await engine.InstallAsync(await Package("0.2.0"), Data);
        Assert.Equal("0.2.0", retry.Version);
    }
    private sealed class Registration : IInstallationRegistration
    {
        public InstallationInfo? Current { get; private set; }
        public string? FailVersion { get; set; }
        public bool FailRemove { get; set; }
        public Task ApplyAsync(InstallationInfo installation, CancellationToken ct)
        {
            if (installation.Version == FailVersion) throw new IOException("Registration fixture failure");
            Current = installation; return Task.CompletedTask;
        }
        public Task RemoveAsync(InstallationInfo installation, CancellationToken ct)
        {
            if (FailRemove) throw new IOException("Registration cleanup fixture failure");
            Current = null; return Task.CompletedTask;
        }
    }
}
