using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class AppUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "play-updates-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(2048);
    private readonly byte[] _setup = Encoding.UTF8.GetBytes("verified setup fixture");
    private string _release = "0.2.0";
    private bool _corrupt;
    private readonly Activity _activity = new();
    private readonly Launcher _launcher = new();
    private readonly List<Uri> _requests = [];
    private PlayPaths Paths => new(_root);
    public void Dispose() { _key.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private AppUpdateManifest Manifest(string? version = null) => new(1, "mcmere-play", version ?? _release, "win-x64", true,
        DateTimeOffset.UtcNow, "更新の検証", AppUpdateCatalog.Asset(version ?? _release, AppUpdateCatalog.SetupName(version ?? _release)),
        _setup.Length, Convert.ToHexString(SHA256.HashData(_setup)).ToLowerInvariant());
    private HttpClient Http() => new(new Handler(request =>
    {
        _requests.Add(request.RequestUri!); Assert.Null(request.Headers.Authorization);
        byte[] bytes;
        if (request.RequestUri!.AbsoluteUri == AppUpdateCatalog.ReleasesApi)
            bytes = DistributionJson.Bytes(new[] { new { draft = false, prerelease = true, tag_name = "v" + _release,
                assets = new[] { new { name = "app-update.json", browser_download_url = AppUpdateCatalog.Asset(_release, "app-update.json") } } } });
        else if (request.RequestUri.AbsoluteUri.EndsWith("app-update.json", StringComparison.Ordinal)) bytes = DistributionJson.Bytes(AppUpdateSigning.Sign(Manifest(), _key));
        else bytes = _corrupt ? new byte[_setup.Length] : _setup;
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    }));
    private AppUpdater Updater(HttpClient http, bool allowPrereleases = true) => new(Paths, _activity, _launcher, http, ManifestSigning.PublicKey(_key), "0.1.0", allowPrereleases);
    [Fact]
    public async Task SignedUpdateWaitsForPrismGameAndOperationsThenHandsOffExactlyOnce()
    {
        using var http = Http(); using var updater = Updater(http);
        await updater.CheckAsync(); Assert.Equal("available", updater.View.Stage);
        await updater.DownloadAsync(); Assert.Equal("ready", updater.View.Stage);
        await updater.QueueAsync(true);
        foreach (var state in new[] { new ActivityState(true, false, false), new ActivityState(false, true, false), new ActivityState(false, false, true) })
        {
            _activity.State = state;
            Assert.False(await updater.TryStartAsync(123, DateTimeOffset.UtcNow, false));
        }
        _activity.State = new(false, false, false);
        Assert.False(await updater.TryStartAsync(123, DateTimeOffset.UtcNow, true));
        Assert.Null(_launcher.Started);
        Assert.True(await updater.TryStartAsync(123, DateTimeOffset.UtcNow, false));
        Assert.False(await updater.TryStartAsync(123, DateTimeOffset.UtcNow, false));
        Assert.Equal(AppUpdater.SetupPath(Paths, Manifest()), _launcher.Started!.FileName);
        Assert.Equal(new[] { "--update-request", AppUpdater.HandoffPath(Paths), "--data-root", Paths.Root }, _launcher.Started.ArgumentList);
        var handoff = await AppUpdateHandoffVerifier.VerifyAsync(AppUpdater.HandoffPath(Paths), Paths, _launcher.Started.FileName, ManifestSigning.PublicKey(_key));
        Assert.Equal("0.2.0", handoff.Manifest.Version);
        Assert.Equal("0.1.0", handoff.Handoff.FromVersion);
    }
    [Fact]
    public async Task CancellingAnUpdateReservationPersistsWithoutDeletingTheDownloadedSetup()
    {
        using var http = Http(); using (var updater = Updater(http))
        {
            await updater.CheckAsync(); await updater.DownloadAsync(); await updater.QueueAsync(true); await updater.QueueAsync(false);
        }
        using var restarted = Updater(http); await restarted.InitializeAsync();
        Assert.Equal("ready", restarted.View.Stage); Assert.False(restarted.View.Queued);
        Assert.False(await restarted.TryStartAsync(123, DateTimeOffset.UtcNow, false));
        Assert.True(File.Exists(AppUpdater.SetupPath(Paths, Manifest())));
    }
    [Fact]
    public async Task DownloadCorruptionAndPostDownloadChangesNeverLaunchASetup()
    {
        using var http = Http(); using var updater = Updater(http);
        await updater.CheckAsync(); _corrupt = true;
        await Assert.ThrowsAsync<DistributionException>(() => updater.DownloadAsync());
        Assert.False(File.Exists(AppUpdater.SetupPath(Paths, Manifest())));
        _corrupt = false; await updater.DownloadAsync(); await updater.QueueAsync(true);
        await File.WriteAllBytesAsync(AppUpdater.SetupPath(Paths, Manifest()), new byte[_setup.Length]);
        Assert.Equal("update_corrupt", (await Assert.ThrowsAsync<DistributionException>(() => updater.TryStartAsync(123, DateTimeOffset.UtcNow, false))).Code);
        Assert.Null(_launcher.Started);
    }
    [Fact]
    public async Task OlderFeedCannotReplaceAnAlreadyVerifiedOfferAcrossRestart()
    {
        using var http = Http(); using (var updater = Updater(http)) await updater.CheckAsync();
        _release = "0.1.5";
        using var restarted = Updater(http); await restarted.InitializeAsync();
        Assert.Equal("update_rollback", (await Assert.ThrowsAsync<DistributionException>(() => restarted.CheckAsync())).Code);
        Assert.Equal("0.2.0", restarted.View.Version);
    }
    [Fact]
    public async Task StableChannelDoesNotDownloadPreviewMetadata()
    {
        using var http = Http(); using var updater = Updater(http, false);
        await updater.CheckAsync(); Assert.Equal("unpublished", updater.View.Stage); Assert.Single(_requests);
    }
    [Fact]
    public async Task AnotherRepositoryCannotSupplyAnUpdateAsset()
    {
        var count = 0;
        using var http = new HttpClient(new Handler(_ => { count++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(DistributionJson.Bytes(new[] {
            new { draft = false, prerelease = true, tag_name = "v0.2.0", assets = new[] { new { name = "app-update.json", browser_download_url = "https://github.com/another/repository/releases/download/v0.2.0/app-update.json" } } }
        })) }; }));
        using var updater = Updater(http); await updater.CheckAsync();
        Assert.Equal("unpublished", updater.View.Stage); Assert.Equal(1, count);
    }
    [Fact]
    public void PackKeysAndAlteredPayloadsCannotAuthorizeApplicationUpdates()
    {
        using var packKey = RSA.Create(2048);
        var envelope = AppUpdateSigning.Sign(Manifest(), _key);
        Assert.Equal("update_signature", Assert.Throws<DistributionException>(() => AppUpdateSigning.Verify(envelope, ManifestSigning.PublicKey(packKey))).Code);
        var altered = envelope with { Payload = Convert.ToBase64String(DistributionJson.Bytes(Manifest("0.3.0"))) };
        Assert.Equal("update_signature", Assert.Throws<DistributionException>(() => AppUpdateSigning.Verify(altered, ManifestSigning.PublicKey(_key))).Code);
        Assert.Throws<DistributionException>(() => AppUpdateSigning.Sign(Manifest() with { SetupUrl = "https://packs.example/arbitrary.exe" }, _key));
        Assert.Throws<DistributionException>(() => AppUpdateSigning.Sign(Manifest() with { Architecture = "linux-x64" }, _key));
    }
    [Fact]
    public async Task HandoffRejectsAnExecutableOutsideTheVerifiedUpdateDirectory()
    {
        using var http = Http(); using var updater = Updater(http);
        await updater.CheckAsync(); await updater.DownloadAsync(); await updater.QueueAsync(true);
        await updater.TryStartAsync(123, DateTimeOffset.UtcNow, false);
        var wrong = Path.Combine(_root, "another.exe"); await File.WriteAllBytesAsync(wrong, _setup);
        Assert.Equal("update_handoff", (await Assert.ThrowsAsync<DistributionException>(() => AppUpdateHandoffVerifier.VerifyAsync(AppUpdater.HandoffPath(Paths), Paths, wrong, ManifestSigning.PublicKey(_key)))).Code);
    }
    private sealed class Activity : IPlayActivity
    {
        public ActivityState State { get; set; } = new(false, false, false);
        public ActivityState Read() => State;
        public Task RequireIdleAsync(string instanceId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Launcher : IAppUpdateLauncher { public ProcessStartInfo? Started; public void Start(ProcessStartInfo start) => Started = start; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request)); }
}
