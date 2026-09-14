using System.Net;
using System.Security.Cryptography;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class DistributionClientTests
{
    [Theory]
    [InlineData("https://packs.example/s/22222222222222222222222222222222")]
    [InlineData("mcmere-play://add?url=https%3A%2F%2Fpacks.example%2Fs%2F22222222222222222222222222222222")]
    public void RegistrationAcceptsOnlyTheIntendedPublicPage(string input)
    {
        var target = DistributionTarget.Parse(input);
        Assert.Equal("https://packs.example", target.Origin);
        Assert.Equal("22222222222222222222222222222222", target.PublicId);
    }
    [Theory]
    [InlineData("http://packs.example/s/22222222222222222222222222222222")]
    [InlineData("https://user:password@packs.example/s/22222222222222222222222222222222")]
    [InlineData("mcmere-play://run?url=https://packs.example/s/22222222222222222222222222222222")]
    [InlineData("mcmere-play://add?url=https://packs.example/s/22222222222222222222222222222222&command=bad")]
    [InlineData("file:///s/22222222222222222222222222222222")]
    public void InvalidRegistrationIsRejected(string input) => Assert.Throws<DistributionException>(() => DistributionTarget.Parse(input));

    [Fact]
    public async Task IdentifiesFetchesSignedVersionAndRechecksBeforeLaunch()
    {
        using var key = RSA.Create(2048);
        var manifest = Fixture.Manifest(); var signed = ManifestSigning.Sign(manifest, key);
        var publicKey = ManifestSigning.PublicKey(key);
        var info = new ServerInfo(manifest.ServerPublicId, "Example", 1, publicKey);
        var target = DistributionTarget.Parse("https://packs.example/s/" + manifest.ServerPublicId);
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.AbsolutePath.Split('/').Last(); calls.Add(path);
            if (path == "info") return Json(info);
            if (path == "sessions") return Json(new DistributionSession("PlayerName", new string('t', 43), DateTimeOffset.UtcNow.AddMinutes(15), info));
            Assert.Equal(new string('t', 43), request.Headers.Authorization?.Parameter);
            return path switch
            {
                "current" => Json(new ReleaseStatus(manifest.ReleaseId, 1, "available", "online", "matched", DateTimeOffset.UtcNow)),
                "launch-check" => Json(new LaunchCheckResult(true, manifest.ReleaseId, null)),
                _ => Json(signed)
            };
        }));
        var client = new DistributionClient(http, target);
        client.Trust((await client.DiscoverAsync()).SigningKey);
        Assert.Equal("PlayerName", (await client.IdentifyAsync("playername")).PlayerName);
        Assert.Equal(manifest.ReleaseId, (await client.FetchAsync()).Manifest.ReleaseId);
        Assert.True((await client.CheckLaunchAsync(manifest.ReleaseId)).Allowed);
        Assert.Equal(new[] { "info", "sessions", "current", manifest.ReleaseId, "launch-check" }, calls);
    }

    [Fact]
    public async Task AChangedPublicKeyIsNotSilentlyTrusted()
    {
        using var key = RSA.Create(2048); using var changed = RSA.Create(2048);
        var target = DistributionTarget.Parse("https://packs.example/s/22222222222222222222222222222222");
        using var http = new HttpClient(new Handler(_ => Json(new ServerInfo(target.PublicId, "Example", 1, ManifestSigning.PublicKey(changed)))));
        var error = await Assert.ThrowsAsync<DistributionException>(() => new DistributionClient(http, target, ManifestSigning.PublicKey(key)).DiscoverAsync());
        Assert.Equal("signing_key_changed", error.Code);
    }

    [Fact]
    public async Task RevokedNameIsNotAllowedToUseACachedSessionAtLaunch()
    {
        using var key = RSA.Create(2048);
        var target = DistributionTarget.Parse("https://packs.example/s/22222222222222222222222222222222");
        var info = new ServerInfo(target.PublicId, "Example", 1, ManifestSigning.PublicKey(key));
        using var http = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("sessions", StringComparison.Ordinal)
            ? Json(new DistributionSession("PlayerName", new string('t', 43), DateTimeOffset.UtcNow.AddMinutes(15), info))
            : Json(new DistributionError("name_not_listed", "名前を確認してください。", false, "test"), HttpStatusCode.Forbidden)));
        var client = new DistributionClient(http, target, info.SigningKey);
        await client.IdentifyAsync("PlayerName");
        Assert.Equal("name_not_listed", (await Assert.ThrowsAsync<DistributionException>(() => client.CheckLaunchAsync("11111111111111111111111111111111"))).Code);
    }
    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new ByteArrayContent(DistributionJson.Bytes(value)) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request)); }
}
