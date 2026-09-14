using System.Net;
using System.Security.Cryptography;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Mcmere.Play.Tests;

public sealed class PlayApplicationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "play-app-" + Guid.NewGuid().ToString("N"));
    private WebApplication _gateway = null!;
    private PlayApplication _play = null!;
    private PackManifest _manifest = null!;
    private string _url = "";
    private bool _allowed = true;
    private RSA _key = null!;
    private ServerInfo _info = null!;
    private SignedManifest _envelope = null!;
    public async ValueTask InitializeAsync()
    {
        _key = RSA.Create(2048);
        _manifest = Fixture.Manifest() with { Java = new(21, "x64", RuntimeCatalog.Java21.Id) };
        _envelope = ManifestSigning.Sign(_manifest, _key);
        _info = new ServerInfo(_manifest.ServerPublicId, "Fixture server", 1, ManifestSigning.PublicKey(_key));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _gateway = builder.Build();
        _gateway.MapGet("/v1/servers/{id}/info", () => Results.Json(_info, DistributionJson.Options));
        _gateway.MapPost("/v1/servers/{id}/sessions", (NameRequest request) => _allowed && request.PlayerName.Equals("PlayerName", StringComparison.OrdinalIgnoreCase)
            ? Results.Json(new DistributionSession("PlayerName", new string('a', 43), DateTimeOffset.UtcNow.AddMinutes(15), _info), DistributionJson.Options)
            : Results.Json(new DistributionError("name_not_listed", "名前を確認してください。", false, "test"), statusCode: 403));
        _gateway.MapGet("/v1/servers/{id}/current", (HttpContext context) =>
            _allowed && context.Request.Headers.Authorization == "Bearer " + new string('a', 43)
            ? Results.Json(new ReleaseStatus(_manifest.ReleaseId, _manifest.Sequence, "available", "online", "matched", DateTimeOffset.UtcNow), DistributionJson.Options)
            : Results.Json(new DistributionError("name_not_listed", "名前を確認してください。", false, "test"), statusCode: 403));
        _gateway.MapGet("/v1/servers/{id}/releases/{release}", () => Results.Json(_envelope, DistributionJson.Options));
        await _gateway.StartAsync();
        _url = _gateway.Urls.Single() + "/s/" + _manifest.ServerPublicId;
        _play = new(new PlayPaths(_root), development: true, activity: new Activity());
    }
    public async ValueTask DisposeAsync()
    {
        _play.Dispose(); _key.Dispose(); await _gateway.StopAsync(); await _gateway.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    [Fact]
    public async Task RegistrationNameAndInspectionUseTheRealHttpContractWithoutDownloadingRuntimes()
    {
        var discovery = await _play.DiscoverAsync(_url);
        var server = await _play.AddAsync(_url, discovery.Info.SigningKey.KeyId);
        await _play.IdentifyAsync(server.Id, "playername");
        var view = await _play.ViewAsync();
        Assert.Equal("PlayerName", view.Settings.Servers!.Single().PlayerName);
        Assert.Equal("setup", view.Servers.Single().Stage);
        Assert.False(view.Servers.Single().JavaReady);
        Assert.False(view.Servers.Single().PrismReady);
        Assert.Equal(_manifest.ReleaseId, view.Servers.Single().Manifest!.ReleaseId);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "runtimes"), "*", SearchOption.AllDirectories));
        using var restarted = new PlayApplication(new PlayPaths(_root), development: true, activity: new Activity());
        Assert.Equal(server.Id, (await restarted.ViewAsync()).Settings.SelectedServer);
    }
    [Fact]
    public async Task RevocationCannotBeHiddenByCachedApplicationState()
    {
        var info = await _play.DiscoverAsync(_url);
        var server = await _play.AddAsync(_url, info.Info.SigningKey.KeyId);
        await _play.IdentifyAsync(server.Id, "PlayerName");
        _allowed = false;
        Assert.Equal("name_not_listed", (await Assert.ThrowsAsync<DistributionException>(() => _play.InspectAsync(server.Id))).Code);
        Assert.Equal("error", (await _play.ViewAsync()).Servers.Single().Stage);
    }
    [Fact]
    public async Task ATransitionSavedDuringRegistrationCannotBeRevertedByTheOldConnection()
    {
        var originalInfo = _info; var originalEnvelope = _envelope; var originalManifest = _manifest;
        var discovery = await _play.DiscoverAsync(_url);
        var saved = await _play.AddAsync(_url, discovery.Info.SigningKey.KeyId);
        await _play.IdentifyAsync(saved.Id, "PlayerName");
        using var next = RSA.Create(2048);
        var nextKey = ManifestSigning.PublicKey(next);
        _info = _info with { SigningKey = nextKey, KeyTransitions = [KeyRotation.Sign(_manifest.ServerPublicId, nextKey, 2, _key)] };
        _manifest = _manifest with { Sequence = 2, ReleaseId = Guid.NewGuid().ToString("N") }; _envelope = ManifestSigning.Sign(_manifest, next);
        await _play.AddAsync(_url, nextKey.KeyId);
        _info = originalInfo; _manifest = originalManifest; _envelope = originalEnvelope;
        await Assert.ThrowsAsync<DistributionException>(() => _play.InspectAsync(saved.Id));
        var stored = (await _play.ViewAsync()).Settings.Servers!.Single();
        Assert.Equal(nextKey, stored.SigningKey); Assert.Equal(2, stored.KeyMinimumSequence);
    }
    [Fact]
    public async Task RemovingRegistrationPreservesDedicatedGameData()
    {
        var info = await _play.DiscoverAsync(_url);
        var server = await _play.AddAsync(_url, info.Info.SigningKey.KeyId);
        var file = PlayFiles.Child(_play.Paths.Game(server.Id), "saves/example.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, "personal");
        await _play.RemoveAsync(server.Id);
        Assert.Empty((await _play.ViewAsync()).Settings.Servers!);
        Assert.Equal("personal", await File.ReadAllTextAsync(file));
    }
    private sealed class Activity : IPlayActivity
    {
        public ActivityState Read() => new(false, false, false);
        public Task RequireIdleAsync(string instanceId, CancellationToken ct) => Task.CompletedTask;
    }
}
