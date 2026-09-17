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
        Assert.Equal("[\"vanilla\",\"mod_resources\",\"file/personal.zip\",\"file/mcmere-example.zip\",\"file/high.zip\"]", patch.After.ResourcePacks);
        Assert.Equal("[\"file/personal.zip\"]", patch.After.IncompatibleResourcePacks);
    }
    [Theory]
    [InlineData("", "[\"mod_resources\",\"file/mcmere-example.zip\"]")]
    [InlineData("resourcePacks:[]\n", "[\"mod_resources\",\"file/mcmere-example.zip\"]")]
    [InlineData("resourcePacks:[\"vanilla\"]\n", "[\"vanilla\",\"mod_resources\",\"file/mcmere-example.zip\"]")]
    [InlineData("resourcePacks:[\"file/mcmere-example.zip\"]\n", "[\"mod_resources\",\"file/mcmere-example.zip\"]")]
    [InlineData("resourcePacks:[\"vanilla\",\"file/mcmere-example.zip\",\"mod_resources\",\"mod/cobblemon\",\"mod/mega_showdown\",\"quark-emote-pack\"]\n",
        "[\"vanilla\",\"mod_resources\",\"mod/cobblemon\",\"mod/mega_showdown\",\"quark-emote-pack\",\"file/mcmere-example.zip\"]")]
    [InlineData("resourcePacks:[\"vanilla\",\"mod_resources\",\"file/personal.zip\",\"file/mcmere-example.zip\"]\n",
        "[\"vanilla\",\"mod_resources\",\"file/personal.zip\",\"file/mcmere-example.zip\"]")]
    public async Task NeoForgeBasePackPrecedesManagedPacksOnFirstInstallAndRepair(string original, string expected)
    {
        var (manifest, _) = await Create();
        var bytes = Encoding.UTF8.GetBytes(original);
        var patch = ResourcePackOptions.Plan(bytes, manifest, null, manifest.Files);
        Assert.Equal(expected, patch.After.ResourcePacks);
        var again = ResourcePackOptions.Plan(ResourcePackOptions.Write(bytes, patch.After), manifest, manifest, manifest.Files);
        Assert.Equal(again.Before, again.After);
    }
    [Fact]
    public async Task NoEnabledManagedPacksDoesNotAddNeoForgeBasePack()
    {
        var (manifest, _) = await Create();
        var bytes = Encoding.UTF8.GetBytes("resourcePacks:[\"vanilla\",\"file/personal.zip\"]\n");
        var patch = ResourcePackOptions.Plan(bytes, manifest, null, []);
        Assert.Equal(patch.Before, patch.After);
        patch = ResourcePackOptions.Plan(bytes, manifest with { Loader = new("fabric", "example") }, null, manifest.Files);
        Assert.Equal("[\"vanilla\",\"file/personal.zip\",\"file/mcmere-example.zip\"]", patch.After.ResourcePacks);
    }
    [Fact]
    public async Task RepairingMissingNeoForgeBasePackDoesNotDownloadOrReplaceZip()
    {
        var (manifest, engine) = await Create();
        await Sync(engine, manifest);
        await File.WriteAllTextAsync(Options, "music:0.3\nresourcePacks:[\"file/mcmere-example.zip\"]\n");
        var plan = await engine.PlanAsync(Instance, manifest, Path.Combine(_root, "java.exe"), 4096);
        Assert.Equal("resourcePackOptions", Assert.Single(plan.Changes).Scope);
        var offline = new SyncEngine(_paths, new Idle(), new UnavailableProvider());
        await Sync(offline, manifest);
        Assert.Equal("[\"mod_resources\",\"file/mcmere-example.zip\"]", ResourcePackOptions.Read(await File.ReadAllBytesAsync(Options)).ResourcePacks);
        Assert.Contains("music:0.3\n", await File.ReadAllTextAsync(Options));
        Assert.Equal(manifest.Files[0].Sha512, await PlayFiles.Sha512Async(Path.Combine(_paths.Game(Instance), manifest.Files[0].Path)));
        Assert.Empty((await offline.PlanAsync(Instance, manifest, Path.Combine(_root, "java.exe"), 4096)).Changes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackUpdateKeepsPriorityAndRejectsCorruptDownloads(bool corrupt)
    {
        var (manifest, engine) = await Create();
        await Sync(engine, manifest);
        var before = await File.ReadAllBytesAsync(Options);
        var source = Path.Combine(_root, "source.zip");
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Update))
        using (var writer = new StreamWriter(zip.CreateEntry("assets/example/lang/ja_jp.json").Open())) writer.Write("{}");
        var bytes = await File.ReadAllBytesAsync(source);
        var file = manifest.Files[0] with { Path = "resourcepacks/mcmere-updated.zip", Length = bytes.Length,
            Sha512 = Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant() };
        var next = manifest with { Sequence = 2, ReleaseId = new string('2', 32), Files = [file],
            PackPairs = [manifest.PackPairs[0] with { ServerArtifactSha512 = file.Sha512 }] };
        if (corrupt)
        {
            bytes[^1] ^= 1;
            await File.WriteAllBytesAsync(source, bytes);
            Assert.Equal("download_corrupt", (await Assert.ThrowsAsync<DistributionException>(() => Sync(engine, next))).Code);
            Assert.Equal(before, await File.ReadAllBytesAsync(Options));
            Assert.Equal(manifest.ReleaseId, (await engine.AppliedAsync(Instance))!.Manifest.ReleaseId);
            Assert.Equal(manifest.Files[0].Sha512, await PlayFiles.Sha512Async(Path.Combine(_paths.Game(Instance), manifest.Files[0].Path)));
            Assert.False(File.Exists(Path.Combine(_paths.Game(Instance), file.Path)));
        }
        else
        {
            await Sync(engine, next);
            Assert.Equal("[\"mod_resources\",\"file/mcmere-updated.zip\"]", ResourcePackOptions.Read(await File.ReadAllBytesAsync(Options)).ResourcePacks);
            Assert.Equal(file.Sha512, await PlayFiles.Sha512Async(Path.Combine(_paths.Game(Instance), file.Path)));
            Assert.False(File.Exists(Path.Combine(_paths.Game(Instance), manifest.Files[0].Path)));
            Assert.Empty((await engine.PlanAsync(Instance, next, Path.Combine(_root, "java.exe"), 4096)).Changes);
        }
    }
    private sealed class UnavailableProvider : IFileProvider
    {
        public Task<string> GetAsync(PackFile file, CancellationToken ct) => throw new InvalidOperationException("No download should be needed to repair pack order.");
    }
    private sealed class Provider(string path) : IFileProvider { public Task<string> GetAsync(PackFile file, CancellationToken ct) => Task.FromResult(path); }
    private sealed class Idle : IInstanceActivity { public Task RequireIdleAsync(string instanceId, CancellationToken ct) => Task.CompletedTask; }
    private sealed class ProgressNow(Action<SyncProgress> report) : IProgress<SyncProgress> { public void Report(SyncProgress value) => report(value); }
}
