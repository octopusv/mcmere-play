using System.Text;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class DataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "play-migration-" + Guid.NewGuid().ToString("N"));
    private readonly Activity _activity = new();
    private PlayPaths Paths => new(Path.Combine(_root, "control"));
    private string Destination => Path.Combine(_root, "移行 data");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static async Task Write(string root, string relative, string text)
    {
        var file = PlayFiles.Child(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, text);
    }
    private async Task<PlayPaths> Prepare()
    {
        var paths = Paths;
        await Write(paths.Root, "settings.json", "{\"schemaVersion\":1,\"theme\":\"dark\",\"servers\":[]}");
        await Write(paths.Root, "prism-data/instances/fixture/.minecraft/saves/keep.txt", "save data");
        await Write(paths.Root, "runtimes/java/fixture/bin/java.exe", "runtime fixture");
        await Write(paths.Root, "prism-data/instances/fixture/instance.cfg", "[General]\r\nJavaPath=" + paths.Root.Replace('\\', '/') + "/runtimes/java/fixture/bin/java.exe\r\nInstanceAccountId=fixture-only-choice\r\nMaxMemAlloc=4096\r\n");
        Directory.CreateDirectory(PlayFiles.Child(paths.Root, "prism-data/instances/fixture/.minecraft/screenshots/empty"));
        return paths;
    }
    [Fact]
    public async Task RelocationPreservesOriginalDataAndSwitchesOnlyTheDataRootWithoutReadingAccountFiles()
    {
        var paths = await Prepare();
        await Write(paths.Root, "app/app-fixture.txt", "application");
        await Write(paths.Root, "updates/keep.txt", "updater");
        await Write(paths.Root, "webview/keep.txt", "webview");
        await Write(paths.Root, "prism-data/accounts.json", "opaque synthetic credentials");
        await Write(paths.Root, "prism-data/accounts-old.json", "opaque synthetic old credentials");
        await Write(paths.Root, "prism-data/prismlauncher.cfg", "global account settings fixture");
        await Write(paths.Root, "runtimes/prism/fixture/accounts.json", "opaque runtime account fixture");
        await Write(paths.Root, "runtimes/prism/fixture/accounts/nested/private.json", "opaque nested account fixture");
        await using var accountLock = new FileStream(Path.Combine(paths.PrismData, "accounts.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var runtimeAccountLock = new FileStream(PlayFiles.Child(paths.Runtimes, "prism/fixture/accounts.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var nestedAccountLock = new FileStream(PlayFiles.Child(paths.Runtimes, "prism/fixture/accounts/nested/private.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var migration = new DataMigration(paths, _activity);
        var receipt = await migration.MigrateAsync(await migration.PlanAsync(Destination));
        var reopened = await PlayPaths.OpenAsync(paths.ControlRoot);
        Assert.Equal(Destination, reopened.Root); Assert.Equal(paths.ControlRoot, reopened.ControlRoot);
        Assert.Equal(Destination, receipt.DataRoot);
        var save = "prism-data/instances/fixture/.minecraft/saves/keep.txt";
        Assert.Equal("save data", await File.ReadAllTextAsync(PlayFiles.Child(paths.Root, save)));
        Assert.Equal("save data", await File.ReadAllTextAsync(PlayFiles.Child(Destination, save)));
        Assert.True(Directory.Exists(PlayFiles.Child(Destination, "prism-data/instances/fixture/.minecraft/screenshots/empty")));
        Assert.False(File.Exists(Path.Combine(reopened.PrismData, "accounts.json")));
        Assert.False(File.Exists(Path.Combine(reopened.PrismData, "accounts-old.json")));
        Assert.False(File.Exists(Path.Combine(reopened.PrismData, "prismlauncher.cfg")));
        Assert.False(File.Exists(PlayFiles.Child(reopened.Runtimes, "prism/fixture/accounts.json")));
        Assert.False(Directory.Exists(PlayFiles.Child(reopened.Runtimes, "prism/fixture/accounts")));
        foreach (var directory in new[] { "app", "updates", "webview" }) Assert.False(Directory.Exists(Path.Combine(Destination, directory)));
        var cfg = await File.ReadAllTextAsync(PlayFiles.Child(Destination, "prism-data/instances/fixture/instance.cfg"));
        Assert.Contains(Destination.Replace('\\', '/') + "/runtimes/java/fixture/bin/java.exe", cfg);
        Assert.DoesNotContain("InstanceAccountId", cfg); Assert.Contains("MaxMemAlloc=4096", cfg);
        Assert.Contains(paths.Root.Replace('\\', '/'), await File.ReadAllTextAsync(PlayFiles.Child(paths.Root, "prism-data/instances/fixture/instance.cfg")));
        Assert.Equal(paths.ControlRoot, Path.GetDirectoryName(Path.GetDirectoryName(AppUpdater.HandoffPath(reopened))));
    }
    [Fact]
    public async Task NonEmptyAndOverlappingDestinationsAreRejectedBeforeAnyCopy()
    {
        var paths = await Prepare(); var migration = new DataMigration(paths, _activity);
        foreach (var destination in new[] { paths.Root, Path.Combine(paths.Root, "nested"), _root })
            Assert.Equal("migration_destination", (await Assert.ThrowsAsync<DistributionException>(() => migration.PlanAsync(destination))).Code);
        Directory.CreateDirectory(Destination); await Write(Destination, "personal.txt", "keep");
        await Assert.ThrowsAsync<DistributionException>(() => migration.PlanAsync(Destination));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(Destination, "personal.txt")));
        Assert.False(File.Exists(DataLocation.Pointer(paths.ControlRoot)));
    }
    [Fact]
    public async Task LowCapacityAndRunningGamePreventMigration()
    {
        var paths = await Prepare();
        Assert.Equal("disk_space", (await Assert.ThrowsAsync<DistributionException>(() => new DataMigration(paths, _activity, new NoSpace()).PlanAsync(Destination))).Code);
        var migration = new DataMigration(paths, _activity); var plan = await migration.PlanAsync(Destination);
        _activity.Busy = true;
        Assert.Equal("game_running", (await Assert.ThrowsAsync<DistributionException>(() => migration.MigrateAsync(plan))).Code);
        Assert.False(Directory.Exists(Destination)); Assert.False(File.Exists(DataLocation.Pointer(paths.ControlRoot)));
    }
    [Fact]
    public async Task CancellationKeepsTheActivePointerAndOriginalData()
    {
        var paths = await Prepare(); var migration = new DataMigration(paths, _activity); var plan = await migration.PlanAsync(Destination);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration.MigrateAsync(plan, new ProgressCallback(value => { if (value.Stage == "copying") cancel.Cancel(); }), cancel.Token));
        Assert.Equal(paths.Root, await DataLocation.ResolveAsync(paths.ControlRoot));
        Assert.False(Directory.Exists(Destination));
        Assert.Empty(Directory.GetDirectories(_root, ".mcmere-play-migration-*"));
        Assert.Equal("save data", await File.ReadAllTextAsync(PlayFiles.Child(paths.Root, "prism-data/instances/fixture/.minecraft/saves/keep.txt")));
    }
    [Fact]
    public async Task ASourceEditDuringCopyNeverSwitchesToAnInconsistentCopy()
    {
        var paths = await Prepare(); var migration = new DataMigration(paths, _activity); var plan = await migration.PlanAsync(Destination);
        var settings = Path.Combine(paths.Root, "settings.json"); var edited = false;
        var error = await Assert.ThrowsAsync<DistributionException>(() => migration.MigrateAsync(plan, new ProgressCallback(value =>
        {
            if (!edited && value.Stage == "verifying") { File.WriteAllText(settings, "edited source settings"); edited = true; }
        })));
        Assert.Equal("migration_changed", error.Code); Assert.False(File.Exists(DataLocation.Pointer(paths.ControlRoot)));
        Assert.Equal("edited source settings", await File.ReadAllTextAsync(settings));
    }
    [Fact]
    public async Task ACompletedCopyWithoutTheFinalPointerCanBeRetried()
    {
        var paths = await Prepare(); var migration = new DataMigration(paths, _activity); var plan = await migration.PlanAsync(Destination);
        var id = Guid.NewGuid().ToString("N"); var stageDirectory = Path.Combine(_root, ".mcmere-play-migration-" + id);
        var stage = new { id, planId = plan.Id, controlRoot = paths.ControlRoot, source = paths.Root, destination = Destination, stageDirectory };
        await Write(Destination, "settings.json", "partial old copy");
        await File.WriteAllBytesAsync(Path.Combine(Destination, "migration-stage.json"), DistributionJson.Bytes(stage));
        await File.WriteAllBytesAsync(Path.Combine(paths.ControlRoot, "migration-pending.json"), DistributionJson.Bytes(stage));
        var receipt = await migration.MigrateAsync(await migration.PlanAsync(Destination));
        Assert.Equal(id, receipt.Id); Assert.Equal(Destination, await DataLocation.ResolveAsync(paths.ControlRoot));
        Assert.Equal(await File.ReadAllTextAsync(Path.Combine(paths.Root, "settings.json")), await File.ReadAllTextAsync(Path.Combine(Destination, "settings.json")));
    }
    [Fact]
    public async Task CorruptTargetAndGameStartingBeforeCommitKeepTheOriginalActive()
    {
        var paths = await Prepare(); var migration = new DataMigration(paths, _activity); var plan = await migration.PlanAsync(Destination);
        var changed = false;
        var error = await Assert.ThrowsAsync<DistributionException>(() => migration.MigrateAsync(plan, new ProgressCallback(value =>
        {
            if (!changed && value.Stage == "verifying")
            {
                var stage = Directory.GetDirectories(_root, ".mcmere-play-migration-*").Single();
                File.WriteAllText(PlayFiles.Child(stage, "prism-data/instances/fixture/.minecraft/saves/keep.txt"), "corrupt target"); changed = true;
            }
        })));
        Assert.Equal("migration_corrupt", error.Code);
        await Assert.ThrowsAsync<DistributionException>(() => migration.MigrateAsync(plan, new ProgressCallback(value => { if (value.Stage == "switching") _activity.Busy = true; })));
        Assert.Equal(paths.Root, await DataLocation.ResolveAsync(paths.ControlRoot));
        Assert.False(Directory.Exists(Destination));
    }
    [Fact]
    public async Task InterruptedTemporaryWritesAreDiscardedBeforeRetryingTheSameMigration()
    {
        var paths = await Prepare(); var migration = new DataMigration(paths, _activity); var plan = await migration.PlanAsync(Destination);
        var id = Guid.NewGuid().ToString("N"); var stageDirectory = Path.Combine(_root, ".mcmere-play-migration-" + id);
        var stage = new { id, planId = plan.Id, controlRoot = paths.ControlRoot, source = paths.Root, destination = Destination, stageDirectory };
        await Write(stageDirectory, "settings.json." + Guid.NewGuid().ToString("N") + ".tmp", "interrupted temporary write");
        await File.WriteAllBytesAsync(Path.Combine(stageDirectory, "migration-stage.json"), DistributionJson.Bytes(stage));
        await File.WriteAllBytesAsync(Path.Combine(paths.ControlRoot, "migration-pending.json"), DistributionJson.Bytes(stage));
        var receipt = await migration.MigrateAsync(plan);
        Assert.Equal(id, receipt.Id);
        Assert.Empty(Directory.GetFiles(Destination, "settings.json.*.tmp"));
        Assert.Equal(Destination, await DataLocation.ResolveAsync(paths.ControlRoot));
    }
    [Fact]
    public async Task MissingMigratedDriveDoesNotSilentlyCreateAnEmptyReplacement()
    {
        var paths = await Prepare(); var migration = new DataMigration(paths, _activity);
        await migration.MigrateAsync(await migration.PlanAsync(Destination));
        Directory.Move(Destination, Destination + "-detached");
        Assert.Equal("data_location_missing", (await Assert.ThrowsAsync<DistributionException>(() => PlayPaths.OpenAsync(paths.ControlRoot))).Code);
        Assert.False(Directory.Exists(Destination));
    }
    [Fact]
    public async Task PendingModTransactionsMustBeRecoveredBeforeRelocation()
    {
        var paths = await Prepare(); await Write(paths.Root, "state/fixture/journal.json", "pending");
        Assert.Equal("recovery_required", (await Assert.ThrowsAsync<DistributionException>(() => new DataMigration(paths, _activity).PlanAsync(Destination))).Code);
        Assert.False(Directory.Exists(Destination));
    }
    private sealed class NoSpace : IAvailableSpace { public long Bytes(string directory) => 0; }
    private sealed class Activity : IPlayActivity
    {
        public bool Busy;
        public ActivityState Read() => new(false, Busy, false);
        public Task RequireIdleAsync(string instanceId, CancellationToken ct) => Busy ? Task.FromException(new DistributionException("game_running", "Game fixture is running.")) : Task.CompletedTask;
    }
    private sealed class ProgressCallback(Action<MigrationProgress> callback) : IProgress<MigrationProgress>
    { public void Report(MigrationProgress value) => callback(value); }
}
