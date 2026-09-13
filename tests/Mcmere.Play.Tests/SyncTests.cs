using System.Text;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "play-sync-" + Guid.NewGuid().ToString("N"));
    private readonly PlayPaths _paths;
    private readonly Provider _provider;
    private readonly Activity _activity = new();
    private readonly Space _space = new();
    private readonly SyncEngine _engine;
    private readonly string _instance = "server-example";
    private readonly string _java;
    public SyncTests()
    {
        _paths = new(_root); _provider = new(_root); _engine = new(_paths, _activity, _provider, _space);
        _java = Path.Combine(_root, "java", "bin", "java.exe");
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string Game(string path) => PlayFiles.Child(_paths.Game(_instance), path);
    private async Task<PackFile> Mod(string id, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var file = Fixture.File(id, bytes);
        await _provider.Add(file, bytes);
        return file;
    }
    private static PackManifest Pack(int sequence, params PackFile[] files) => Fixture.Manifest() with
    { ReleaseId = sequence.ToString("x32"), Sequence = sequence, Files = files };
    private Task<AppliedPack> Sync(PackManifest manifest, IProgress<SyncProgress>? progress = null, CancellationToken ct = default) =>
        _engine.SynchronizeAsync(_instance, manifest, _java, 4096, progress: progress, ct: ct);

    [Fact]
    public async Task InstallsAndUpdatesOnlyOwnedFilesWhileKeepingSettingsAndSaves()
    {
        var a = await Mod("a", "version one");
        await Sync(Pack(1, a));
        Directory.CreateDirectory(Path.GetDirectoryName(Game("saves/world.dat"))!);
        await File.WriteAllTextAsync(Game("saves/world.dat"), "personal world");
        await File.WriteAllTextAsync(Game("options.txt"), "personal settings");
        var b = await Mod("b", "version two");
        await Sync(Pack(2, b));
        Assert.False(File.Exists(Game(a.Path)));
        Assert.Equal("version two", await File.ReadAllTextAsync(Game(b.Path)));
        Assert.Equal("personal world", await File.ReadAllTextAsync(Game("saves/world.dat")));
        Assert.Equal("personal settings", await File.ReadAllTextAsync(Game("options.txt")));
        Assert.Equal(2, (await _engine.AppliedAsync(_instance))!.Manifest.Sequence);
        Assert.Contains(PlayFiles.Files(_paths.Backups), file => file.EndsWith("a.jar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorruptPreparedFileLeavesThePreviousEnvironmentAndManifestIntact()
    {
        var a = await Mod("a", "old"); await Sync(Pack(1, a));
        var b = await Mod("a", "new"); _provider.Corrupt = true;
        Assert.Equal("download_corrupt", (await Assert.ThrowsAsync<DistributionException>(() => Sync(Pack(2, b)))).Code);
        Assert.Equal("old", await File.ReadAllTextAsync(Game(a.Path)));
        Assert.Equal(1, (await _engine.AppliedAsync(_instance))!.Manifest.Sequence);
    }

    [Fact]
    public async Task InterruptedApplyRecoversCreatedReplacedAndRemovedFilesOnNextStart()
    {
        var a = await Mod("a", "old a"); var b = await Mod("b", "old b");
        await Sync(Pack(1, a, b));
        var a2 = await Mod("a", "new a"); var c = await Mod("c", "new c");
        using var cancel = new CancellationTokenSource();
        var progress = new ImmediateProgress(update => { if (update.Stage == "applying" && update.Completed == 3) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sync(Pack(2, a2, c), progress, cancel.Token));
        Assert.True(File.Exists(PlayFiles.Child(_paths.State, _instance + "/journal.json")));
        await _engine.RecoverAsync(_instance);
        Assert.Equal("old a", await File.ReadAllTextAsync(Game(a.Path)));
        Assert.Equal("old b", await File.ReadAllTextAsync(Game(b.Path)));
        Assert.False(File.Exists(Game(c.Path)));
        Assert.Equal(1, (await _engine.AppliedAsync(_instance))!.Manifest.Sequence);
        await Sync(Pack(2, a2, c));
        Assert.Equal("new a", await File.ReadAllTextAsync(Game(a.Path)));
        Assert.False(File.Exists(Game(b.Path)));
        Assert.Equal("new c", await File.ReadAllTextAsync(Game(c.Path)));
    }

    [Fact]
    public async Task RecoveryDoesNotOverwriteAFurtherUserEdit()
    {
        var a = await Mod("a", "old"); await Sync(Pack(1, a));
        var a2 = await Mod("a", "new");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sync(Pack(2, a2), new ImmediateProgress(update =>
        { if (update.Stage == "applying") cancel.Cancel(); }), cancel.Token));
        await File.WriteAllTextAsync(Game(a.Path), "edited after interruption");
        Assert.Equal("recovery_conflict", (await Assert.ThrowsAsync<DistributionException>(() => _engine.RecoverAsync(_instance))).Code);
        Assert.Equal("edited after interruption", await File.ReadAllTextAsync(Game(a.Path)));
    }

    [Fact]
    public async Task UnknownModsRequireExplicitQuarantineAndRemainAvailableInBackup()
    {
        var a = await Mod("a", "required"); await Sync(Pack(1, a));
        await File.WriteAllTextAsync(Game("mods/personal.jar"), "personal mod");
        var plan = await _engine.PlanAsync(_instance, Pack(1, a), _java, 4096);
        Assert.Single(plan.UnknownMods);
        Assert.Equal("unknown_mods", (await Assert.ThrowsAsync<DistributionException>(() => Sync(Pack(1, a)))).Code);
        await _engine.SynchronizeAsync(_instance, Pack(1, a), _java, 4096, quarantineUnknown: true);
        Assert.False(File.Exists(Game("mods/personal.jar")));
        Assert.Contains(PlayFiles.Files(_paths.Backups), path => path.EndsWith("personal.jar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeedConfigurationIsNotOverwrittenOrRemovedOnLaterVersions()
    {
        var a = await Mod("config", "seed one");
        a = a with { Path = "config/example.toml", ModIds = [], UpdatePolicy = FileUpdatePolicy.Seed };
        await Sync(Pack(1, a));
        await File.WriteAllTextAsync(Game(a.Path), "personal config");
        var next = a with { Version = "2" };
        await Sync(Pack(2, next));
        Assert.Equal("personal config", await File.ReadAllTextAsync(Game(a.Path)));
        await Sync(Pack(3));
        Assert.Equal("personal config", await File.ReadAllTextAsync(Game(a.Path)));
    }

    [Fact]
    public async Task LackOfSpaceAndRunningGamePreventWritesAndDownloads()
    {
        var a = await Mod("a", "a");
        _space.Available = 1;
        Assert.Equal("disk_space", (await Assert.ThrowsAsync<DistributionException>(() => Sync(Pack(1, a)))).Code);
        Assert.Equal(0, _provider.Calls);
        _space.Available = long.MaxValue; _activity.Busy = true;
        Assert.Equal("game_running", (await Assert.ThrowsAsync<DistributionException>(() => Sync(Pack(1, a)))).Code);
        Assert.False(File.Exists(Game(a.Path)));
    }

    [Fact]
    public async Task ReviewedPlanCannotOverwriteSubsequentLocalChanges()
    {
        var a = await Mod("a", "old"); await Sync(Pack(1, a));
        var next = Pack(2, await Mod("a", "new"));
        var plan = await _engine.PlanAsync(_instance, next, _java, 4096);
        await File.WriteAllTextAsync(Game(a.Path), "changed since review");
        Assert.Equal("plan_changed", (await Assert.ThrowsAsync<DistributionException>(() => _engine.SynchronizeAsync(_instance, next, _java, 4096, expectedPlanId: plan.Id))).Code);
        Assert.Equal("changed since review", await File.ReadAllTextAsync(Game(a.Path)));
    }

    [Fact]
    public async Task KeepsTwoCompletedBackupGenerationsAfterSuccessfulUpdates()
    {
        for (var i = 1; i <= 4; i++) await Sync(Pack(i, await Mod("a", "version " + i)));
        var root = PlayFiles.Child(_paths.Backups, _instance);
        Assert.Equal(2, Directory.EnumerateDirectories(root).Count());
        Assert.Equal("version 4", await File.ReadAllTextAsync(Game("mods/a.jar")));
    }

    [Fact]
    public async Task CheckingAnUnchangedPackDoesNotPruneMeaningfulBackups()
    {
        await Sync(Pack(1, await Mod("a", "old")));
        var manifest = Pack(2, await Mod("a", "new"));
        var applied = await Sync(manifest);
        var backups = Directory.GetDirectories(PlayFiles.Child(_paths.Backups, _instance)).Order().ToArray();
        _space.Available = 0;
        Assert.Equal(applied.TransactionId, (await Sync(manifest)).TransactionId);
        Assert.Equal(backups, Directory.GetDirectories(PlayFiles.Child(_paths.Backups, _instance)).Order().ToArray());
    }

    [Fact]
    public async Task IfGameStartsDuringUpdateRecoveryWaitsUntilItStops()
    {
        var a = await Mod("a", "old"); await Sync(Pack(1, a));
        var next = Pack(2, await Mod("a", "new"), await Mod("b", "new b"));
        var progress = new ImmediateProgress(update => { if (update.Stage == "applying") _activity.Busy = true; });
        Assert.Equal("game_running", (await Assert.ThrowsAsync<DistributionException>(() => Sync(next, progress))).Code);
        Assert.Equal("game_running", (await Assert.ThrowsAsync<DistributionException>(() => _engine.RecoverAsync(_instance))).Code);
        _activity.Busy = false;
        await _engine.RecoverAsync(_instance);
        Assert.Equal("old", await File.ReadAllTextAsync(Game(a.Path)));
        Assert.False(File.Exists(Game("mods/b.jar")));
    }

    private sealed class Provider(string root) : IFileProvider
    {
        private readonly Dictionary<string, string> _files = new();
        public bool Corrupt { get; set; }
        public int Calls { get; private set; }
        public async Task Add(PackFile file, byte[] bytes)
        {
            var path = Path.Combine(root, "source-" + file.Sha512); await File.WriteAllBytesAsync(path, bytes); _files[file.Sha512] = path;
        }
        public async Task<string> GetAsync(PackFile file, CancellationToken ct)
        {
            Calls++;
            var path = _files[file.Sha512];
            if (Corrupt) await File.WriteAllTextAsync(path, "wrong bytes", ct);
            return path;
        }
    }
    private sealed class Activity : IInstanceActivity
    {
        public bool Busy { get; set; }
        public Task RequireIdleAsync(string instanceId, CancellationToken ct)
        { if (Busy) throw new DistributionException("game_running", "Game is running"); return Task.CompletedTask; }
    }
    private sealed class Space : IAvailableSpace
    {
        public long Available { get; set; } = long.MaxValue;
        public long Bytes(string directory) => Available;
    }
    private sealed class ImmediateProgress(Action<SyncProgress> report) : IProgress<SyncProgress>
    { public void Report(SyncProgress value) => report(value); }
}
