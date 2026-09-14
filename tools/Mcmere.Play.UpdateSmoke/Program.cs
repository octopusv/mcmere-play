using System.Diagnostics;
using System.Net;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

if (args.Length is not (4 or 5) || args.Length == 5 && args[4] != "--prepare-only") throw new ArgumentException("Usage: UpdateSmoke <data-root> <signed-manifest> <setup> <report> [--prepare-only]");
var root = Path.GetFullPath(args[0]);
if (!root.Contains(Path.DirectorySeparatorChar + ".test-data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Use isolated .test-data.");
var paths = await PlayPaths.OpenAsync(root, requireIsolated: true);
var envelopeBytes = await File.ReadAllBytesAsync(args[1]);
var envelope = DistributionJson.Read<SignedManifest>(envelopeBytes);
var manifest = AppUpdateSigning.Verify(envelope, AppUpdateCatalog.TrustedKey);
var setup = Path.GetFullPath(args[2]);
var previous = await InstallationEngine.ReadInstallationAsync(Path.Combine(root, "app")) ?? throw new InvalidOperationException("Install the previous version first.");
if (!await AppUpdater.VerifySetupAsync(setup, manifest)) throw new InvalidOperationException("Fixture Setup does not match signed manifest.");
var report = Path.GetFullPath(args[3]);
using var http = new HttpClient(new FixtureHttp(manifest, envelopeBytes, setup));
var launcher = new TestLauncher(report);
using var updater = new AppUpdater(paths, new ProcessActivity(paths), launcher, http, currentVersion: previous.Version);
await using var appLock = new FileStream(Path.Combine(root, "application.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
await updater.CheckAsync(); await updater.DownloadAsync();
if (args.Length == 5)
{
    await PlayFiles.WriteAtomicAsync(Path.Combine(root, "updates", "probe-result.json"), DistributionJson.Bytes(new {
        success = true, transport = "synthetic release HTTP responses", manifestSignatureVerified = true, downloadedSetupVerified = true,
        expectedVersion = manifest.Version, preparedForNativeApp = true, gameConnectionTested = false
    }));
    return;
}
await updater.QueueAsync(true);
using var own = Process.GetCurrentProcess();
if (!await updater.TryStartAsync(Environment.ProcessId, new DateTimeOffset(own.StartTime.ToUniversalTime()), false)) throw new InvalidOperationException("Update handoff was blocked.");
var acknowledgement = Path.Combine(root, "updates", "handoff-status.json");
var until = DateTimeOffset.UtcNow.AddSeconds(60);
while (!File.Exists(acknowledgement) && DateTimeOffset.UtcNow < until)
{
    if (File.Exists(report)) throw new InvalidOperationException("Setup exited before waiting for its parent: " + report);
    await Task.Delay(200);
}
if (!File.Exists(acknowledgement)) throw new TimeoutException("Setup has not acknowledged the handoff.");
if (File.Exists(report)) throw new InvalidOperationException("Setup installed while its parent was still running.");
await PlayFiles.WriteAtomicAsync(Path.Combine(root, "updates", "probe-result.json"), DistributionJson.Bytes(new {
    success = true, transport = "synthetic release HTTP responses", manifestSignatureVerified = true, downloadedSetupVerified = true,
    parentWaitAcknowledged = true, setupProcessId = launcher.ProcessId, expectedVersion = manifest.Version, gameConnectionTested = false
}));
Console.WriteLine("Verified Setup is waiting for this process to exit.");

sealed class TestLauncher(string report) : IAppUpdateLauncher
{
    public int ProcessId { get; private set; }
    public void Start(ProcessStartInfo start)
    {
        foreach (var value in new[] { "--test-mode", "--report", report }) start.ArgumentList.Add(value);
        start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Setup did not start.");
        ProcessId = process.Id;
    }
}
sealed class FixtureHttp(AppUpdateManifest manifest, byte[] envelope, string setup) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.Authorization is not null) throw new InvalidOperationException("App updates must not send pack sessions.");
        HttpContent content;
        if (request.RequestUri!.AbsoluteUri == AppUpdateCatalog.ReleasesApi)
            content = new ByteArrayContent(DistributionJson.Bytes(new[] { new { draft = false, prerelease = manifest.Prerelease, tag_name = "v" + manifest.Version,
                assets = new[] { new { name = "app-update.json", browser_download_url = AppUpdateCatalog.Asset(manifest.Version, "app-update.json") } } } }));
        else if (request.RequestUri.AbsoluteUri == AppUpdateCatalog.Asset(manifest.Version, "app-update.json")) content = new ByteArrayContent(envelope);
        else if (request.RequestUri.AbsoluteUri == manifest.SetupUrl) content = new StreamContent(File.OpenRead(setup));
        else throw new InvalidOperationException("Unexpected test request: " + request.RequestUri);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
