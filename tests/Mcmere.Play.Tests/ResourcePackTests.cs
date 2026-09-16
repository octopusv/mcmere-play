using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class ResourcePackTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, ".test-data", "resource-packs-" + Guid.NewGuid().ToString("N"));
    private readonly PlayPaths _paths;
    private const string Instance = "example";
    public ResourcePackTests() { _paths = new(_root); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string Options => Path.Combine(_paths.Game(Instance), "options.txt");
    private async Task<(PackManifest Manifest, SyncEngine Engine)> Create(int sequence = 1)
    {
        var source = Path.Combine(_root, "source.zip");
        if (!File.Exists(source))
        {
            using var zip = ZipFile.Open(source, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("pack.mcmeta").Open())) writer.Write("{\"pack\":{\"pack_format\":48,\"supported_formats\":[34,48]}}");
            using var asset = new StreamWriter(zip.CreateEntry("assets/example/lang/en_us.json").Open()); asset.Write("{}");
        }
        var bytes = await File.ReadAllBytesAsync(source);
        var file = Fixture.File("resources", bytes) with { Path = "resourcepacks/mcmere-example.zip", ModIds = [] };
        var manifest = Fixture.Manifest() with { SchemaVersion = 2, FingerprintVersion = 2, DeploymentId = new string('1', 32),
            Sequence = sequence, ReleaseId = sequence.ToString("x32"), Files = [file], RequiredFeatures = ManifestValidation.PackFeatures,
            ResourcePacks = [new("example", file.Id)], PackPairs = [new("example", "sameArtifact", file.Sha512, file.Id)] };
        return (manifest, new(_paths, new Idle(), new Provider(source)));
    }
    private Task<AppliedPack> Sync(SyncEngine engine, PackManifest manifest, IProgress<SyncProgress>? progress = null, CancellationToken ct = default) =>
        engine.SynchronizeAsync(Instance, manifest, Path.Combine(_root, "java.exe"), 4096, progress: progress, ct: ct);

    [Fact]
    public async Task SyncActivatesPackPreservesPersonalSettingsAndRepairsOnlySelection()
    {
        var (manifest, engine) = await Create(); Directory.CreateDirectory(Path.GetDirectoryName(Options)!);
        var personal = "\uFEFFmusic:0.3\r\nkey_jump:key.keyboard.space\r\nresourcePacks:[\"vanilla\",\"file/personal.zip\"]\r\n";
        await File.WriteAllBytesAsync(Options, Encoding.UTF8.GetBytes(personal));
        await Sync(engine, manifest);
        var text = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Options));
        Assert.StartsWith("\uFEFFmusic:0.3\r\nkey_jump:key.keyboard.space\r\n", text);
        Assert.Contains("file/personal.zip", text); Assert.Contains("file/mcmere-example.zip", text);
        Assert.Empty((await engine.PlanAsync(Instance, manifest, Path.Combine(_root, "java.exe"), 4096)).Changes);
        await File.WriteAllBytesAsync(Options, Encoding.UTF8.GetBytes(personal));
        var plan = await engine.PlanAsync(Instance, manifest, Path.Combine(_root, "java.exe"), 4096);
        Assert.Equal("resourcePackOptions", Assert.Single(plan.Changes).Scope);
        await Sync(engine, manifest);
        Assert.Contains("file/mcmere-example.zip", await File.ReadAllTextAsync(Options));
    }
    [Fact]
    public async Task RemovingManagedPackRetainsPersonalResourcePacks()
    {
        var (manifest, engine) = await Create(); await Sync(engine, manifest);
        var next = manifest with { Sequence = 2, ReleaseId = new string('2', 32), Files = [], ResourcePacks = [], PackPairs = [] };
        await File.AppendAllTextAsync(Options, "music:0.1\n");
        await Sync(engine, next);
        Assert.DoesNotContain("mcmere-example", await File.ReadAllTextAsync(Options));
        Assert.Contains("music:0.1", await File.ReadAllTextAsync(Options));
        Assert.False(File.Exists(Path.Combine(_paths.Game(Instance), manifest.Files[0].Path)));
    }
    [Fact]
    public async Task InterruptedOptionsUpdateRestoresSelectionButKeepsLaterVolumeChanges()
    {
        var (manifest, engine) = await Create(); Directory.CreateDirectory(Path.GetDirectoryName(Options)!);
        await File.WriteAllTextAsync(Options, "music:0.4\nresourcePacks:[\"vanilla\"]\n");
        using var cancel = new CancellationTokenSource();
        var progress = new ProgressNow(update => { if (update.Stage == "applying" && File.ReadAllText(Options).Contains("mcmere-example")) cancel.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sync(engine, manifest, progress, cancel.Token));
        var text = (await File.ReadAllTextAsync(Options)).Replace("music:0.4", "music:0.9", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Options, text);
        await engine.RecoverAsync(Instance);
        Assert.Equal("music:0.9\nresourcePacks:[\"vanilla\"]\n", await File.ReadAllTextAsync(Options));
        Assert.Null(await engine.AppliedAsync(Instance));
    }
    [Theory]
    [InlineData("resourcePacks:invalid\n")]
    [InlineData("resourcePacks:[]\nresourcePacks:[]\n")]
    public async Task InvalidOptionsAreNeverReset(string original)
    {
        var (manifest, engine) = await Create(); Directory.CreateDirectory(Path.GetDirectoryName(Options)!); await File.WriteAllTextAsync(Options, original);
        Assert.Equal("options_conflict", (await Assert.ThrowsAsync<DistributionException>(() => Sync(engine, manifest))).Code);
        Assert.Equal(original, await File.ReadAllTextAsync(Options));
    }
    [Fact]
    public async Task ContractRejectsDifferentPairedBytesAndUnknownFeatures()
    {
        var (manifest, _) = await Create(); ManifestValidation.Validate(manifest);
        Assert.Equal("pack_pair_mismatch", Assert.Throws<DistributionException>(() => ManifestValidation.Validate(manifest with { PackPairs = [manifest.PackPairs[0] with { ServerArtifactSha512 = new string('a', 128) }] })).Code);
        Assert.Equal("unsupported_feature", Assert.Throws<DistributionException>(() => ManifestValidation.Validate(manifest with { RequiredFeatures = ["unknown"] })).Code);
        Assert.Equal("unsupported_schema", Assert.Throws<DistributionException>(() => ManifestValidation.Validate(manifest with { SchemaVersion = 1 })).Code);
    }
    [Fact]
    public async Task HighPriorityFirstBecomesLastInOptionsAndDoesNotForceIncompatiblePacks()
    {
        var (manifest, _) = await Create(); var high = manifest.Files[0] with { Id = "high", Path = "resourcepacks/high.zip" };
        manifest = manifest with { Files = [manifest.Files[0], high], ResourcePacks = [new("high", "high"), manifest.ResourcePacks[0]] };
        var bytes = Encoding.UTF8.GetBytes("resourcePacks:[\"vanilla\",\"file/personal.zip\"]\nincompatibleResourcePacks:[\"file/high.zip\",\"file/personal.zip\"]\n");
        var patch = ResourcePackOptions.Plan(bytes, manifest, null, manifest.Files);
        Assert.Equal("[\"vanilla\",\"file/personal.zip\",\"file/mcmere-example.zip\",\"file/high.zip\"]", patch.After.ResourcePacks);
        Assert.Equal("[\"file/personal.zip\"]", patch.After.IncompatibleResourcePacks);
    }
    private sealed class Provider(string path) : IFileProvider { public Task<string> GetAsync(PackFile file, CancellationToken ct) => Task.FromResult(path); }
    private sealed class Idle : IInstanceActivity { public Task RequireIdleAsync(string instanceId, CancellationToken ct) => Task.CompletedTask; }
    private sealed class ProgressNow(Action<SyncProgress> report) : IProgress<SyncProgress> { public void Report(SyncProgress value) => report(value); }
}
