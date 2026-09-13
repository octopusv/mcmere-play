using System.Text;
using System.Text.Json.Nodes;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class ProfileAndSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "play-settings-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Fact]
    public void CachedPrismMetadataAndHarmlessFormattingArePreservedExactly()
    {
        var java = Path.GetFullPath(Path.Combine(_root, "java", "bin", "java.exe"));
        var manifest = Fixture.Manifest();
        var first = PrismProfile.Prepare(manifest, java, 4096);
        var pack = JsonNode.Parse(first["mmc-pack.json"])!.AsObject();
        pack["components"]![0]!["cachedName"] = "Minecraft";
        pack["components"]![0]!["cachedVersion"] = "1.21.1";
        pack["components"]!.AsArray().Add(new JsonObject { ["uid"] = "org.lwjgl3", ["version"] = "3.3.3", ["dependencyOnly"] = true });
        var cached = pack.ToJsonString();
        var cfg = Encoding.UTF8.GetString(first["instance.cfg"]).Replace("\n", "\r\n", StringComparison.Ordinal) + "InstanceAccountId=account-choice\r\nlastLaunchTime=123\r\n";
        var prepared = PrismProfile.Prepare(manifest, java, 4096, cfg, cached);
        Assert.Equal(cfg, Encoding.UTF8.GetString(prepared["instance.cfg"]));
        Assert.Equal(cached, Encoding.UTF8.GetString(prepared["mmc-pack.json"]));
    }
    [Fact]
    public void ASecondLoaderOrDisabledNeoForgeIsNotAcceptedAsMatching()
    {
        var manifest = Fixture.Manifest();
        var json = JsonNode.Parse(PrismProfile.Prepare(manifest, Path.GetFullPath(Path.Combine(_root, "java.exe")), 4096)["mmc-pack.json"])!;
        json["components"]![1]!["disabled"] = true;
        Assert.False(PrismProfile.PackMatches(json.ToJsonString(), manifest));
        json["components"]![1]!["disabled"] = false;
        json["components"]!.AsArray().Add(new JsonObject { ["uid"] = "net.fabricmc.fabric-loader", ["version"] = "0.1" });
        Assert.False(PrismProfile.PackMatches(json.ToJsonString(), manifest));
    }
    [Fact]
    public async Task SettingsPersistAcrossRestartsWithoutStoringSessionTokens()
    {
        var paths = new PlayPaths(_root);
        var store = new PlaySettingsStore(paths);
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var target = DistributionTarget.Parse("https://packs.example/s/22222222222222222222222222222222");
        var server = new SavedServer(paths.InstanceId(target.BaseUri, target.PublicId), target, "Example", ManifestSigning.PublicKey(key), "PlayerName");
        await store.UpdateAsync(value => value with { Servers = [server], SelectedServer = server.Id, Theme = "dark" });
        var read = await new PlaySettingsStore(paths).ReadAsync();
        Assert.Equal(server.Id, read.SelectedServer); Assert.Equal("dark", read.Theme);
        var json = await File.ReadAllTextAsync(Path.Combine(_root, "settings.json"));
        Assert.DoesNotContain("sessionToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access_token", json, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task AlteredServerIdentityIsRejectedWithoutOverwritingValidSettings()
    {
        var paths = new PlayPaths(_root); var store = new PlaySettingsStore(paths);
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var target = DistributionTarget.Parse("https://packs.example/s/22222222222222222222222222222222");
        var server = new SavedServer("wrong-directory", target, "Example", ManifestSigning.PublicKey(key));
        await Assert.ThrowsAsync<DistributionException>(() => store.UpdateAsync(value => value with { Servers = [server] }));
        Assert.False(File.Exists(Path.Combine(_root, "settings.json")));
    }
    [Fact]
    public void RecommendedModPreferenceSurvivesAFileNameAndVersionChange()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var first = Fixture.File("old") with { Requirement = FileRequirement.Recommended, Source = new() { Kind = FileSourceKind.Modrinth, Url = "https://cdn.modrinth.com/old.jar", ProjectId = "project" } };
        var next = first with { Id = "new", Path = "mods/new-version.jar", Version = "2" };
        var server = new SavedServer("id", new("https://packs.example", "22222222222222222222222222222222"), "Example", ManifestSigning.PublicKey(key),
            OptionalChoices: new Dictionary<string, bool> { [PlaySettingsStore.ChoiceKey(first)] = false });
        Assert.Empty(PlaySettingsStore.Selected(Fixture.Manifest() with { Files = [next] }, server));
    }
}
