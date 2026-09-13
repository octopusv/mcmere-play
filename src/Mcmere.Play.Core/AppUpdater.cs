using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record AppUpdateView(string Stage = "idle", string? Version = null, string? Notes = null, long Length = 0,
    long Received = 0, bool Queued = false, string? Error = null);
internal sealed record SavedAppUpdate(SignedManifest Envelope, bool Queued);
public sealed record AppUpdateHandoff(SignedManifest Envelope, string FromVersion, int ParentProcessId, DateTimeOffset ParentStartedAt);
public interface IAppUpdateLauncher { void Start(ProcessStartInfo start); }
public sealed class AppUpdateLauncher : IAppUpdateLauncher
{
    public void Start(ProcessStartInfo start) { using var process = Process.Start(start) ?? throw new DistributionException("update_start", "更新用セットアップを起動できません。"); }
}

public sealed class AppUpdater : IDisposable
{
    private readonly PlayPaths _paths;
    private readonly IPlayActivity _activity;
    private readonly IAppUpdateLauncher _launcher;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DistributionKey _key;
    private readonly string _version;
    private readonly bool _allowPrereleases;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SavedAppUpdate? _saved;
    private AppUpdateManifest? _manifest;
    private bool _started;
    public AppUpdateView View { get; private set; } = new();
    public event Action? Changed;
    public AppUpdater(PlayPaths paths, IPlayActivity activity, IAppUpdateLauncher? launcher = null, HttpClient? http = null,
        DistributionKey? trustedKey = null, string? currentVersion = null, bool allowPrereleases = true)
    {
        _paths = paths; _activity = activity; _launcher = launcher ?? new AppUpdateLauncher();
        _ownsHttp = http is null; _http = http ?? VerifiedDownloads.CreateHttpClient();
        _key = trustedKey ?? AppUpdateCatalog.TrustedKey; _version = currentVersion ?? PlayVersion.Current; _allowPrereleases = allowPrereleases;
        if (!AppUpdateCatalog.IsVersion(_version)) throw new ArgumentException("Invalid application version.");
    }
    private string StateFile => PlayFiles.Child(_paths.Root, "updates/state.json");
    public static string SetupPath(PlayPaths paths, AppUpdateManifest manifest) => PlayFiles.Child(paths.Root, "updates/setup-" + manifest.Version + "-" + manifest.Sha256 + ".exe");
    public static string HandoffPath(PlayPaths paths) => PlayFiles.Child(paths.Root, "updates/handoff.json");
    private void Set(AppUpdateView value) { View = value; Changed?.Invoke(); }
    public Task InitializeAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        if (!File.Exists(StateFile)) return;
        if (new FileInfo(StateFile).Length > AppUpdateSigning.MaximumEnvelopeBytes + 1024) throw new DistributionException("update_state", "保存したアプリ更新情報が不正です。");
        var saved = DistributionJson.Read<SavedAppUpdate>(await File.ReadAllBytesAsync(StateFile, ct));
        var manifest = AppUpdateSigning.Verify(saved.Envelope, _key);
        if (Version.Parse(manifest.Version) <= Version.Parse(_version)) { Set(new("current")); return; }
        if (!_allowPrereleases && manifest.Prerelease) throw new DistributionException("update_channel", "このアプリでは検証版の更新を適用できません。");
        _manifest = manifest; _saved = saved;
        var ready = await VerifySetupAsync(SetupPath(_paths, manifest), manifest, ct);
        Set(new(ready ? "ready" : "available", manifest.Version, manifest.Notes, manifest.Length, Queued: saved.Queued && ready));
    }, ct);
    public Task CheckAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        if (View.Queued) throw new DistributionException("update_queued", "更新予約を取り消してから再確認してください。");
        Set(View with { Stage = "checking", Error = null });
        var data = await ReadMetadataAsync(new(AppUpdateCatalog.ReleasesApi), 8 * 1024 * 1024, ct);
        if (data is null) { Set(View with { Stage = _manifest is null ? "unpublished" : "available" }); return; }
        using var releases = JsonDocument.Parse(data);
        if (releases.RootElement.ValueKind != JsonValueKind.Array || releases.RootElement.GetArrayLength() > 100) throw new DistributionException("update_feed", "アプリの公開情報が不正です。");
        var candidates = new List<(string Version, bool Prerelease)>();
        var published = false;
        foreach (var release in releases.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            var preview = release.GetProperty("prerelease").GetBoolean();
            if (preview && !_allowPrereleases) continue;
            var tag = release.GetProperty("tag_name").GetString();
            if (tag is null || !tag.StartsWith('v') || !AppUpdateCatalog.IsVersion(tag[1..])) continue;
            var version = tag[1..];
            var asset = release.GetProperty("assets").EnumerateArray().Where(item => item.GetProperty("name").GetString() == "app-update.json").ToArray();
            if (asset.Length != 1 || asset[0].GetProperty("browser_download_url").GetString() != AppUpdateCatalog.Asset(version, "app-update.json")) continue;
            published = true;
            if (Version.Parse(version) <= Version.Parse(_version)) continue;
            candidates.Add((version, preview));
        }
        if (candidates.Count == 0) { Set(View with { Stage = _manifest is null ? published ? "current" : "unpublished" : "available", Error = null }); return; }
        var candidate = candidates.OrderByDescending(value => Version.Parse(value.Version)).First();
        if (_manifest is not null && Version.Parse(candidate.Version) < Version.Parse(_manifest.Version)) throw new DistributionException("update_rollback", "以前に確認した版より古いアプリ更新が指定されています。");
        var signedBytes = await ReadMetadataAsync(new(AppUpdateCatalog.Asset(candidate.Version, "app-update.json")), AppUpdateSigning.MaximumEnvelopeBytes, ct)
            ?? throw new DistributionException("update_missing", "署名済みの更新情報が見つかりません。");
        var envelope = DistributionJson.Read<SignedManifest>(signedBytes);
        var manifest = AppUpdateSigning.Verify(envelope, _key);
        if (manifest.Version != candidate.Version || manifest.Prerelease != candidate.Prerelease) throw new DistributionException("update_manifest", "公開版と署名済みの更新情報が一致しません。");
        _manifest = manifest; _saved = new(envelope, false);
        await PlayFiles.WriteAtomicAsync(StateFile, DistributionJson.Bytes(_saved), ct);
        Set(new(await VerifySetupAsync(SetupPath(_paths, manifest), manifest, ct) ? "ready" : "available", manifest.Version, manifest.Notes, manifest.Length));
    }, ct);
    public Task DownloadAsync(CancellationToken ct = default) => RunAsync(async () =>
    {
        var manifest = _manifest ?? throw new DistributionException("update_check", "先にアプリの更新を確認してください。");
        Set(View with { Stage = "downloading", Received = 0, Error = null });
        var downloader = new VerifiedDownloads(_http, new DownloadPolicy());
        var cache = await downloader.GetAsync(new("play-update", manifest.SetupUrl, manifest.Length, manifest.Sha256, "sha256"),
            PlayFiles.Child(_paths.Root, "updates/cache"), progress: new Transfer(value => Set(View with { Received = value.Received })), ct: ct);
        var destination = SetupPath(_paths, manifest);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(cache, temp, false);
            if (!await VerifySetupAsync(temp, manifest, ct)) throw new DistributionException("update_corrupt", "更新用セットアップが破損しています。");
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Set(View with { Stage = "ready", Received = manifest.Length });
    }, ct);
    public Task QueueAsync(bool queued, CancellationToken ct = default) => RunAsync(async () =>
    {
        if (_saved is null || _manifest is null || View.Stage != "ready") throw new DistributionException("update_not_ready", "先にアプリ更新を準備してください。");
        _saved = _saved with { Queued = queued };
        await PlayFiles.WriteAtomicAsync(StateFile, DistributionJson.Bytes(_saved), ct);
        Set(View with { Queued = queued, Error = null });
    }, ct);
    public async Task<bool> TryStartAsync(int parentProcessId, DateTimeOffset parentStartedAt, bool applicationBusy, CancellationToken ct = default)
    {
        if (!View.Queued || _started || applicationBusy || !await _gate.WaitAsync(0, ct)) return false;
        try
        {
            var activity = _activity.Read();
            if (activity.GameRunning || activity.PrismRunning || activity.Uncertain) return false;
            var manifest = _manifest ?? throw new DistributionException("update_not_ready", "アプリ更新を再確認してください。");
            var setup = SetupPath(_paths, manifest);
            if (!await VerifySetupAsync(setup, manifest, ct)) throw new DistributionException("update_corrupt", "更新用セットアップが変更されました。再取得してください。");
            var handoff = new AppUpdateHandoff(_saved!.Envelope, _version, parentProcessId, parentStartedAt);
            await PlayFiles.WriteAtomicAsync(HandoffPath(_paths), DistributionJson.Bytes(handoff), ct);
            var start = new ProcessStartInfo(setup) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(setup)!, WindowStyle = ProcessWindowStyle.Normal };
            foreach (var argument in new[] { "--update-request", HandoffPath(_paths), "--data-root", _paths.Root }) start.ArgumentList.Add(argument);
            _launcher.Start(start); _started = true; Set(View with { Stage = "applying" }); return true;
        }
        catch (Exception error) { Set(View with { Queued = false, Error = Message(error) }); throw; }
        finally { _gate.Release(); }
    }
    public static async Task<bool> VerifySetupAsync(string file, AppUpdateManifest manifest, CancellationToken ct = default)
    {
        PlayFiles.NoLinksToRoot(file);
        if (!File.Exists(file) || new FileInfo(file).Length != manifest.Length) return false;
        await using var input = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant() == manifest.Sha256;
    }
    private async Task<byte[]?> ReadMetadataAsync(Uri uri, int maximum, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        for (var hop = 0; hop < 6; hop++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
                !(uri.AbsoluteUri == AppUpdateCatalog.ReleasesApi || uri.Host is "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new DistributionException("update_origin", "アプリ更新情報の配布元を確認できません。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("mcmere-play/" + _version);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.RequestMessage?.RequestUri is { } effective && effective != uri) throw new DistributionException("automatic_redirect", "更新用HTTPクライアントの設定が不正です。");
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location ?? throw new DistributionException("update_redirect", "アプリ更新情報の転送先がありません。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) throw new DistributionException("update_network", "アプリ更新を確認できません (HTTP " + (int)response.StatusCode + ")。時間をおいて再試行してください。");
            if (response.Content.Headers.ContentLength > maximum) throw new DistributionException("update_size", "アプリ更新情報が大きすぎます。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + count > maximum) throw new DistributionException("update_size", "アプリ更新情報が大きすぎます。");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        throw new DistributionException("update_redirect", "アプリ更新情報の転送が多すぎます。");
    }
    private async Task RunAsync(Func<Task> action, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new DistributionException("update_busy", "アプリ更新の処理中です。");
        try { await action(); }
        catch (Exception error)
        {
            Set(View with { Stage = _manifest is null ? "idle" : "available", Queued = false, Error = Message(error) });
            throw new DistributionException(error is DistributionException domain ? domain.Code : "update_failed", Message(error));
        }
        finally { _gate.Release(); }
    }
    private static string Message(Exception error) => error is DistributionException domain ? domain.Message : "アプリ更新を完了できません。通信状態を確認して再試行してください。";
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
    private sealed class Transfer(Action<TransferProgress> report) : IProgress<TransferProgress> { public void Report(TransferProgress value) => report(value); }
}
