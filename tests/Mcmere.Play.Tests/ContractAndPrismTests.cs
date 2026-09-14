using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class ContractAndPrismTests
{
    [Fact]
    public void SignedManifestRequiresTheTrustedKeyExactBytesAndExpectedServer()
    {
        using var key = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var manifest = Fixture.Manifest();
        var envelope = ManifestSigning.Sign(manifest, key);
        var trusted = ManifestSigning.PublicKey(key);
        Assert.Equal(manifest.ReleaseId, ManifestSigning.Verify(envelope, trusted, manifest.ServerPublicId).ReleaseId);
        Assert.Throws<DistributionException>(() => ManifestSigning.Verify(envelope, ManifestSigning.PublicKey(other), manifest.ServerPublicId));
        Assert.Throws<DistributionException>(() => ManifestSigning.Verify(envelope, trusted, Guid.NewGuid().ToString("N")));
        var changed = envelope with { Payload = Convert.ToBase64String(DistributionJson.Bytes(manifest with { GameEndpoint = "other.example" })) };
        Assert.Throws<DistributionException>(() => ManifestSigning.Verify(changed, trusted, manifest.ServerPublicId));
        Assert.Throws<DistributionException>(() => ManifestSigning.Verify(envelope, trusted, manifest.ServerPublicId, minimumSequence: 2));
    }
    [Fact]
    public void OptionalDependencyOfARequiredModCannotBeDeselected()
    {
        var dependency = Fixture.File("kotlin") with { Requirement = FileRequirement.Recommended, DefaultEnabled = false };
        var main = Fixture.File("cobblemon") with { Requires = [dependency.Id] };
        var selection = ManifestValidation.Selected(Fixture.Manifest() with { Files = [main, dependency] }, new HashSet<string>());
        Assert.Equal(2, selection.Count);
    }
    [Fact]
    public void OptionalDependencyCycleTerminatesAndIncludesEachFileOnlyOnce()
    {
        var a = Fixture.File("a") with { Requires = ["b"] };
        var b = Fixture.File("b") with { Requirement = FileRequirement.Recommended, Requires = ["a"] };
        Assert.Equal(2, ManifestValidation.Selected(Fixture.Manifest() with { Files = [a, b] }).Count);
    }
    [Theory]
    [InlineData("../mods/x.jar")]
    [InlineData("mods/../../x.jar")]
    [InlineData("mods/CON.jar")]
    [InlineData("mods/x.jar:stream")]
    [InlineData("mods\\x.jar")]
    [InlineData("mods/ x.jar.")]
    [InlineData("/mods/x.jar")]
    [InlineData("mods//x.jar")]
    public void UnsafePathsAreRejectedBeforeAnyWrite(string path) => Assert.Throws<DistributionException>(() => ManifestValidation.RelativePath(path));

    [Fact]
    public void SavesAndCaseCollisionsCannotEnterThePack()
    {
        Assert.Throws<DistributionException>(() => ManifestValidation.Validate(Fixture.Manifest() with { Files = [Fixture.File("a") with { Path = "saves/world.dat" }] }));
        Assert.Throws<DistributionException>(() => ManifestValidation.Validate(Fixture.Manifest() with { Files = [Fixture.File("a"), Fixture.File("b") with { Path = "mods/A.jar" }] }));
    }
    [Fact]
    public void DuplicateModProvidersAndUnresolvedDependenciesAreRejected()
    {
        Assert.Throws<DistributionException>(() => ManifestValidation.Validate(Fixture.Manifest() with { Files = [Fixture.File("a") with { ModIds = ["shared"] }, Fixture.File("b") with { ModIds = ["shared"] }] }));
        Assert.Throws<DistributionException>(() => ManifestValidation.Validate(Fixture.Manifest() with { Files = [Fixture.File("a") with { Requires = ["missing"] }] }));
    }
    [Fact]
    public void PrismProfilePinsMinecraftNeoForgeAndTheActualJavaExecutable()
    {
        var java = Path.GetFullPath(Path.Combine("runtime space", "bin", "java.exe"));
        var files = PrismProfile.Prepare(Fixture.Manifest(), java, 4096, "[General]\nInstanceAccountId=keep-selected-account\nPreLaunchCommand=unexpected\n");
        using var profile = JsonDocument.Parse(files["mmc-pack.json"]);
        var components = profile.RootElement.GetProperty("components").EnumerateArray().ToArray();
        Assert.Equal("net.minecraft", components[0].GetProperty("uid").GetString());
        Assert.Equal("1.21.1", components[0].GetProperty("version").GetString());
        Assert.Equal("net.neoforged", components[1].GetProperty("uid").GetString());
        Assert.Equal("21.1.250", components[1].GetProperty("version").GetString());
        var cfg = Encoding.UTF8.GetString(files["instance.cfg"]);
        Assert.Contains("JavaPath=" + java.Replace('\\', '/'), cfg);
        Assert.Contains("AutomaticJava=false", cfg);
        Assert.Contains("InstanceAccountId=keep-selected-account", cfg);
        Assert.DoesNotContain("unexpected", cfg);
    }
    [Fact]
    public void DifferentDistributorsCannotShareOneServerInstanceDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "play-paths-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PlayPaths(root);
            var server = Guid.NewGuid().ToString("N");
            Assert.NotEqual(paths.InstanceId(new Uri("https://one.example"), server), paths.InstanceId(new Uri("https://two.example"), server));
            var instance = paths.InstanceId(new Uri("https://one.example"), server);
            var command = PrismProfile.Launch(Path.Combine(root, "Prism Launcher", "prismlauncher.exe"), paths, instance, "game.example:25565");
            Assert.False(command.UseShellExecute);
            Assert.Equal(paths.PrismData, command.ArgumentList[1]);
            Assert.Equal(instance, command.ArgumentList[3]);
            Assert.Equal("game.example:25565", command.ArgumentList[5]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

internal static class Fixture
{
    public static PackManifest Manifest() => new()
    {
        ReleaseId = "11111111111111111111111111111111", Sequence = 1, ServerPublicId = "22222222222222222222222222222222",
        ServerName = "Example server", CreatedAt = DateTimeOffset.UtcNow, DisplayVersion = "1", MinecraftVersion = "1.21.1",
        Loader = new("neoforge", "21.1.250"), Java = new(21, "x64", "temurin-21"), GameEndpoint = "game.example:25565",
        ServerFingerprint = new('a', 64), ClientFingerprint = new('b', 64), Files = [File("example")]
    };
    public static PackFile File(string id, byte[]? bytes = null) => new()
    {
        Id = id, Path = "mods/" + id + ".jar", Name = id, Version = "1.0.0", Length = (bytes ?? [1, 2, 3]).Length,
        Sha512 = Convert.ToHexString(SHA512.HashData(bytes ?? [1, 2, 3])).ToLowerInvariant(),
        Source = new() { Kind = FileSourceKind.Direct, Url = "https://cdn.modrinth.com/" + id + ".jar" }, ModIds = [id]
    };
}
