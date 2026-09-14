using System.Net;
using System.Security.Cryptography;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class KeyRotationTests
{
    private const string Server = "22222222222222222222222222222222";
    [Fact]
    public void AnOfflineClientCanVerifyMultipleForwardTransitionsButNotReplayEarlierTrust()
    {
        using var first = RSA.Create(2048); using var second = RSA.Create(2048); using var third = RSA.Create(2048);
        var initial = ManifestSigning.PublicKey(first); var middle = ManifestSigning.PublicKey(second); var last = ManifestSigning.PublicKey(third);
        var transitions = new[] { KeyRotation.Sign(Server, middle, 2, first), KeyRotation.Sign(Server, last, 5, second) };
        Assert.Equal(new RotatedTrust(last, 5), KeyRotation.Verify(initial, last, transitions, Server));
        Assert.Equal(new RotatedTrust(last, 5), KeyRotation.Verify(middle, last, transitions, Server, 2));
        Assert.Throws<DistributionException>(() => KeyRotation.Verify(last, middle, transitions, Server, 5));
        Assert.Throws<DistributionException>(() => KeyRotation.Verify(initial, last, transitions.Reverse().ToArray(), Server));
        Assert.Throws<DistributionException>(() => KeyRotation.Verify(initial, last, transitions, new string('3', 32)));
    }
    [Fact]
    public void TamperedWrongSignerAndCyclicTransitionsAreRejected()
    {
        using var first = RSA.Create(2048); using var second = RSA.Create(2048); using var other = RSA.Create(2048);
        var initial = ManifestSigning.PublicKey(first); var next = ManifestSigning.PublicKey(second);
        var valid = KeyRotation.Sign(Server, next, 2, first);
        Assert.Throws<DistributionException>(() => KeyRotation.Verify(initial, next, [valid with { Sha256 = new string('a', 64) }], Server));
        Assert.Throws<DistributionException>(() => KeyRotation.Verify(initial, next, [KeyRotation.Sign(Server, next, 2, other)], Server));
        Assert.Throws<DistributionException>(() => KeyRotation.Verify(initial, next, [valid, KeyRotation.Sign(Server, initial, 3, second)], Server));
        Assert.Throws<DistributionException>(() => KeyRotation.Verify(initial, next, Enumerable.Repeat(valid, 17).ToArray(), Server));
        Assert.Throws<DistributionException>(() => KeyRotation.Verify(initial, next, [valid], Server, 3));
    }
    [Fact]
    public async Task CachedNameSessionRefreshesTrustWhenTheNextManifestUsesANewKey()
    {
        using var old = RSA.Create(2048); using var next = RSA.Create(2048);
        var manifest = Fixture.Manifest(); var info = new ServerInfo(manifest.ServerPublicId, "Example", 1, ManifestSigning.PublicKey(old));
        var envelope = ManifestSigning.Sign(manifest, old);
        var discoveryCalls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            var name = request.RequestUri!.Segments.Last();
            if (name == "info") { discoveryCalls++; return Reply(info); }
            if (name == "sessions") return Reply(new DistributionSession("PlayerName", new string('s', 43), DateTimeOffset.UtcNow.AddMinutes(15), info));
            if (name == "current") return Reply(new ReleaseStatus(manifest.ReleaseId, manifest.Sequence, "available", "online", "matched", DateTimeOffset.UtcNow));
            return Reply(envelope);
        }));
        var client = new DistributionClient(http, new("https://packs.example", manifest.ServerPublicId), info.SigningKey);
        await client.IdentifyAsync("PlayerName"); await client.FetchAsync();
        var transition = KeyRotation.Sign(manifest.ServerPublicId, ManifestSigning.PublicKey(next), 2, old);
        info = info with { SigningKey = ManifestSigning.PublicKey(next), KeyTransitions = [transition] };
        manifest = manifest with { Sequence = 2, ReleaseId = new string('3', 32) }; envelope = ManifestSigning.Sign(manifest, next);
        Assert.Equal(2, (await client.FetchAsync()).Manifest.Sequence);
        Assert.Equal(info.SigningKey, client.TrustedKey); Assert.Equal(2, client.KeyMinimumSequence); Assert.Equal(1, discoveryCalls);
        manifest = manifest with { Sequence = 1 }; envelope = ManifestSigning.Sign(manifest, next);
        Assert.Equal("release_outdated", (await Assert.ThrowsAsync<DistributionException>(() => client.FetchAsync())).Code);
    }
    [Fact]
    public async Task NewSessionAcceptsOnlyAValidTransitionAndAnExplicitResetRequiresReregistration()
    {
        using var old = RSA.Create(2048); using var next = RSA.Create(2048);
        var target = new DistributionTarget("https://packs.example", Server);
        var info = new ServerInfo(Server, "Example", 1, ManifestSigning.PublicKey(next));
        using var http = new HttpClient(new Handler(_ => Reply(new DistributionSession("PlayerName", new string('s', 43), DateTimeOffset.UtcNow.AddMinutes(15), info))));
        var client = new DistributionClient(http, target, ManifestSigning.PublicKey(old));
        Assert.Equal("signing_key_changed", (await Assert.ThrowsAsync<DistributionException>(() => client.IdentifyAsync("PlayerName"))).Code);
        info = info with { KeyTransitions = [KeyRotation.Sign(Server, info.SigningKey, 4, old)] };
        await client.IdentifyAsync("PlayerName"); Assert.Equal(4, client.KeyMinimumSequence);
    }
    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(DistributionJson.Bytes(value)) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
}
