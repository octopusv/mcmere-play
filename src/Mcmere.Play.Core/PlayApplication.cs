using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record ServerView(string Id, string Stage = "idle", string? Error = null, string? ErrorCode = null,
    PackManifest? Manifest = null, ReleaseStatus? Status = null, SyncPlan? Plan = null, bool JavaReady = false,
    bool PrismReady = false, string? Directory = null, long Received = 0, long Total = 0, string? CurrentFile = null);
public sealed record PlayView(PlaySettings Settings, IReadOnlyList<ServerView> Servers, bool Busy, bool CanCancel, ActivityState Activity, string Version,
    AppUpdateView? Update = null, string? DataRoot = null, MigrationProgress? Migration = null);
public sealed record ServerDiscovery(DistributionTarget Target, ServerInfo Info);
public interface IGameLauncher { void Start(ProcessStartInfo start); }
public sealed class GameLauncher : IGameLauncher
{
    public void Start(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new DistributionException("launch_failed", "Prismを起動できません。");
    }
}

public sealed class PlayApplication : IDisposable
{
    private sealed class Connection : IDisposable
    {
        private readonly HttpClient _api = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        private readonly HttpClient _files = VerifiedDownloads.CreateHttpClient();
        public DistributionClient Client { get; }
        public VerifiedDownloads Downloads { get; }
        public Connection(SavedServer server, bool development)
        {
            _api.DefaultRequestHeaders.UserAgent.ParseAdd("mcmere-play/" + PlayVersion.Current);
            _files.DefaultRequestHeaders.UserAgent.ParseAdd("mcmere-play/" + PlayVersion.Current);
            Client = new(_api, server.Target, server.SigningKey, keyMinimumSequence: server.KeyMinimumSequence);
            Downloads = new(_files, new DownloadPolicy(server.Target.BaseUri, development));
        }
        public void Dispose() { _api.Dispose(); _files.Dispose(); }
    }
    private readonly PlayPaths _paths;
    private readonly PlaySettingsStore _settings;
    private readonly IPlayActivity _activity;
    private readonly IGameLauncher _launcher;
    private readonly bool _development;
    private readonly HttpClient _runtimeHttp = VerifiedDownloads.CreateHttpClient();
    private readonly RuntimeManager _runtimes;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ServerView> _views = new(StringComparer.Ordinal);
    private readonly List<string> _reuse = [];
    private CancellationTokenSource? _active;
    private bool _busy;
    private bool _canCancel;
    private string? _launchedServer;
    private bool _wasGameRunning;
    public event Action? Changed;
    public PlayPaths Paths => _paths;
    public async Task<IReadOnlyList<RuntimeDiagnostic>> RuntimeDiagnosticsAsync(CancellationToken ct = default)
    {
        var result = new List<RuntimeDiagnostic>();
        foreach (var (kind, artifact) in new[] { ("java", RuntimeCatalog.Java21), ("prism", RuntimeCatalog.Prism) })
        {
            try
            {
                var installation = await _runtimes.FindAsync(artifact, kind, ct);
                result.Add(new(kind, installation?.Version, installation is not null));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DistributionException)
            { result.Add(new(kind, null, false, (error as DistributionException)?.Code ?? "operation_failed")); }
        }
        return result;
    }
    public PlayApplication(PlayPaths paths, bool development = false, IPlayActivity? activity = null, IGameLauncher? launcher = null)
    {
        _paths = paths; _development = development; _settings = new(paths, development);
        _activity = activity ?? new ProcessActivity(paths); _launcher = launcher ?? new GameLauncher();
        _runtimeHttp.DefaultRequestHeaders.UserAgent.ParseAdd("mcmere-play/" + PlayVersion.Current);
        _runtimes = new(paths, new VerifiedDownloads(_runtimeHttp, new DownloadPolicy()));
    }
    public async Task<PlayView> ViewAsync(CancellationToken ct = default)
    {
        var settings = await _settings.ReadAsync(ct);
        var activity = _activity.Read();
        if (activity.GameRunning && _launchedServer is { } launched && settings.Servers!.Any(server => server.Id == launched && !server.GameObserved))
            settings = await UpdateServerAsync(launched, server => server with { GameObserved = true }, ct);
        if (_wasGameRunning && !activity.GameRunning && _launchedServer is { } finished && Current(finished).Stage == "launching")
            Set(finished, Current(finished) with { Stage = "ready" });
        _wasGameRunning = activity.GameRunning;
        return new(settings, settings.Servers!.Select(server => _views.GetValueOrDefault(server.Id) ?? new ServerView(server.Id)).ToArray(), _busy, _canCancel, activity, PlayVersion.Current, DataRoot: _paths.Root);
    }
    public Task<MigrationPlan> PlanMigrationAsync(string destination, CancellationToken ct = default) => Task.Run(() => new DataMigration(_paths, _activity).PlanAsync(destination, ct), ct);
    public async Task<MigrationReceipt> MigrateDataAsync(MigrationPlan plan, IProgress<MigrationProgress>? progress = null, CancellationToken ct = default)
    {
        if (!await _operation.WaitAsync(0, ct)) throw new DistributionException("operation_busy", "別の処理が終わってから保存先を変更してください。");
        using var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _active = source; _busy = true; _canCancel = true; Changed?.Invoke();
        try
        {
            return await Task.Run(() => new DataMigration(_paths, _activity).MigrateAsync(plan, new CallbackProgress<MigrationProgress>(value =>
            {
                _canCancel = value.Stage != "switching"; progress?.Report(value); Changed?.Invoke();
            }), source.Token), source.Token);
        }
        finally { _active = null; _busy = false; _canCancel = false; _operation.Release(); Changed?.Invoke(); }
    }
    public async Task<ServerDiscovery> DiscoverAsync(string url, CancellationToken ct = default)
    {
        var target = DistributionTarget.Parse(url, _development);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        var info = await new DistributionClient(http, target).DiscoverAsync(ct);
        return new(target, info);
    }
    public async Task<SavedServer> AddAsync(string url, string expectedKeyId, CancellationToken ct = default)
    {
        var discovery = await DiscoverAsync(url, ct);
        if (discovery.Info.SigningKey.KeyId != expectedKeyId) throw new DistributionException("signing_key_changed", "配布元の公開鍵が変わりました。確認し直してください。");
        var id = _paths.InstanceId(discovery.Target.BaseUri, discovery.Target.PublicId);
        var saved = new SavedServer(id, discovery.Target, discovery.Info.Name, discovery.Info.SigningKey);
        var settings = await _settings.UpdateAsync(current =>
        {
            var existing = current.Servers!.FirstOrDefault(server => server.Id == id);
            if (existing is null) return current with { Servers = current.Servers!.Append(saved).ToArray(), SelectedServer = id };
            var trust = KeyRotation.Verify(existing.SigningKey, saved.SigningKey, discovery.Info.KeyTransitions, discovery.Target.PublicId, existing.KeyMinimumSequence);
            return current with { Servers = current.Servers!.Select(item => item.Id == id ? item with { SigningKey = trust.Key, KeyMinimumSequence = trust.MinimumSequence } : item).ToArray(), SelectedServer = id };
        }, ct);
        Changed?.Invoke();
        return settings.Servers!.Single(server => server.Id == id);
    }
    public async Task SelectAsync(string id, CancellationToken ct = default)
    {
        await RequireServerAsync(id, ct);
        await _settings.UpdateAsync(settings => settings with { SelectedServer = id }, ct);
        Changed?.Invoke();
    }
    public async Task SetThemeAsync(string theme, CancellationToken ct = default)
    {
        await _settings.UpdateAsync(settings => settings with { Theme = theme }, ct); Changed?.Invoke();
    }
    public async Task RestoreSettingsAsync(CancellationToken ct = default)
    {
        if (!await _operation.WaitAsync(0, ct)) throw new DistributionException("operation_busy", "処理が終わってから設定を復元してください。");
        try
        {
            await _settings.RestorePreviousAsync(ct);
            foreach (var connection in _connections.Values) connection.Dispose();
            _connections.Clear(); _views.Clear(); Changed?.Invoke();
        }
        finally { _operation.Release(); }
    }
    public async Task SetMemoryAsync(string id, int memoryMiB, CancellationToken ct = default)
    {
        if (_busy) throw new DistributionException("operation_busy", "処理が終わってから設定してください。");
        await UpdateServerAsync(id, server => server with { MemoryMiB = memoryMiB }, ct);
        Set(id, Current(id) with { Stage = "idle", Plan = null }); Changed?.Invoke();
    }
    public async Task SetOptionalAsync(string id, string fileId, bool enabled, CancellationToken ct = default)
    {
        if (_busy) throw new DistributionException("operation_busy", "処理が終わってから設定してください。");
        var manifest = Current(id).Manifest ?? throw new DistributionException("inspect_required", "先に配布情報を確認してください。");
        var file = manifest.Files.SingleOrDefault(file => file.Id == fileId) ?? throw new DistributionException("file_not_found", "MODが見つかりません。");
        var required = ManifestValidation.Selected(manifest, new HashSet<string>()).Select(file => file.Id).ToHashSet();
        if (!enabled && required.Contains(fileId)) throw new DistributionException("required_mod", "このMODは参加に必要です。");
        await UpdateServerAsync(id, server =>
        {
            var choices = server.OptionalChoices is null ? new Dictionary<string, bool>() : new Dictionary<string, bool>(server.OptionalChoices);
            choices[PlaySettingsStore.ChoiceKey(file)] = enabled;
            return server with { OptionalChoices = choices };
        }, ct);
        Set(id, Current(id) with { Stage = "idle", Plan = null });
    }
    public void AddReuseFolder(string directory)
    {
        if (_busy) throw new DistributionException("operation_busy", "処理が終わってからフォルダーを選択してください。");
        PlayFiles.NoLinksToRoot(directory);
        if (!System.IO.Directory.Exists(directory)) throw new DistributionException("folder_missing", "フォルダーが見つかりません。");
        if (!_reuse.Contains(directory, StringComparer.OrdinalIgnoreCase)) _reuse.Add(Path.GetFullPath(directory));
    }
    public Task ImportFileAsync(string id, string fileId, string sourcePath, CancellationToken ct = default) => RunAsync(id, async token =>
    {
        await RequireServerAsync(id, token);
        var file = Current(id).Manifest?.Files.FirstOrDefault(file => file.Id == fileId) ?? throw new DistributionException("file_not_found", "配布ファイルが見つかりません。");
        PlayFiles.NoLinksToRoot(sourcePath);
        if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length != file.Length || await PlayFiles.Sha512Async(sourcePath, token) != file.Sha512)
            throw new DistributionException("file_mismatch", "選択したファイルは必要なバージョンと一致しません。");
        var target = PlayFiles.Child(_paths.Cache, "sha512-" + file.Sha512);
        using var cacheLock = await CacheAccess.AcquireAsync(target, token);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = File.OpenRead(sourcePath))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await input.CopyToAsync(output, token);
            if (await PlayFiles.Sha512Async(temp, token) != file.Sha512) throw new DistributionException("file_changed", "コピー中にファイルが変更されました。");
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Set(id, Current(id) with { Stage = "idle", Error = null, ErrorCode = null });
    }, ct);
    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        if (_busy) throw new DistributionException("operation_busy", "処理が終わってから登録を外してください。");
        await RequireServerAsync(id, ct);
        await _settings.UpdateAsync(settings =>
        {
            var remaining = settings.Servers!.Where(server => server.Id != id).ToArray();
            return settings with { Servers = remaining, SelectedServer = settings.SelectedServer == id ? remaining.FirstOrDefault()?.Id : settings.SelectedServer };
        }, ct);
        if (_connections.TryRemove(id, out var connection)) connection.Dispose();
        _views.TryRemove(id, out _); Changed?.Invoke();
    }
    public Task IdentifyAsync(string id, string name, CancellationToken ct = default) => RunAsync(id, async token =>
    {
        var server = await RequireServerAsync(id, token);
        var connection = Connect(server);
        var session = await connection.Client.IdentifyAsync(name, token);
        await UpdateServerAsync(id, saved => saved with { PlayerName = session.PlayerName }, token);
        await InspectCoreAsync(id, token);
    }, ct);
    public Task InspectAsync(string id, CancellationToken ct = default) => RunAsync(id, token => InspectCoreAsync(id, token), ct);
    public Task PrepareAsync(string id, bool launch, bool quarantineUnknown = false, CancellationToken ct = default) => RunAsync(id, async token =>
    {
        var fetched = await FetchAsync(id, token);
        var server = await RequireServerAsync(id, token);
        var connection = Connect(server);
        var java = await _runtimes.FindAsync(RuntimeCatalog.Java(fetched.Manifest.Java), "java", token);
        var prism = await _runtimes.FindAsync(RuntimeCatalog.Prism, "prism", token);
        if (java is null || prism is null)
        {
            await _activity.RequireIdleAsync(id, token);
            Set(id, Current(id) with { Stage = "runtime" });
            prism = await _runtimes.EnsurePrismAsync(Transfer(id), token);
            java = await _runtimes.EnsureJavaAsync(fetched.Manifest.Java, Transfer(id), token);
        }
        await RuntimeManager.InspectJavaAsync(java.Executable, fetched.Manifest.Java.Major, token);
        var engine = Engine(connection);
        var choices = PlaySettingsStore.Selected(fetched.Manifest, server);
        SyncPlan plan;
        try { plan = await engine.InspectAsync(id, fetched.Manifest, java.Executable, server.MemoryMiB, choices, quarantineUnknown, token); }
        catch (DistributionException error) when (error.Code == "recovery_required")
        {
            Set(id, Current(id) with { Stage = "recovering" });
            await engine.RecoverAsync(id, token);
            plan = await engine.InspectAsync(id, fetched.Manifest, java.Executable, server.MemoryMiB, choices, quarantineUnknown, token);
        }
        Set(id, Current(id) with { Plan = plan, JavaReady = true, PrismReady = true, Directory = _paths.Game(id), Stage = "preparing" });
        if (plan.UnknownMods.Count > 0 && !quarantineUnknown) throw new DistributionException("unknown_mods", "追加されたMODがあります。退避して標準構成に戻すか確認してください。");
        var applied = await engine.AppliedAsync(id, token);
        if (plan.Changes.Count > 0 || applied?.Manifest.ReleaseId != fetched.Manifest.ReleaseId)
        {
            await engine.SynchronizeAsync(id, fetched.Manifest, java.Executable, server.MemoryMiB, choices, quarantineUnknown, plan.Id,
                new CallbackProgress<SyncProgress>(update =>
                {
                    _canCancel = update.Stage != "applying";
                    Set(id, Current(id) with { Stage = update.Stage == "applying" ? "applying" : "preparing" });
                }), token);
        }
        _canCancel = true;
        Set(id, Current(id) with { Stage = server.GameObserved ? "ready" : "prismSetup", Plan = plan with { Changes = [], UnknownMods = [] }, Received = 0, Total = 0, CurrentFile = null });
        if (launch) await LaunchCoreAsync(id, prism, token);
    }, ct);
    public Task LaunchAsync(string id, CancellationToken ct = default) => PrepareAsync(id, true, ct: ct);
    public async Task OpenPrismAsync(string id, CancellationToken ct = default)
    {
        var server = await RequireServerAsync(id, ct);
        var prism = await _runtimes.FindAsync(RuntimeCatalog.Prism, "prism", ct) ?? throw new DistributionException("setup_required", "先にプレイ環境を準備してください。");
        var start = new ProcessStartInfo(prism.Executable) { UseShellExecute = false, WorkingDirectory = prism.Directory };
        foreach (var argument in new[] { "--dir", _paths.PrismData, "--show", server.Id }) start.ArgumentList.Add(argument);
        _launcher.Start(start);
    }
    public void Cancel() { if (_canCancel) _active?.Cancel(); }
    public async Task<IReadOnlyList<JsonElement>> HistoryAsync(string id, CancellationToken ct = default)
    {
        await RequireServerAsync(id, ct);
        var root = PlayFiles.Child(_paths.State, id + "/history");
        if (!System.IO.Directory.Exists(root)) return [];
        var result = new List<JsonElement>();
        foreach (var file in System.IO.Directory.EnumerateFiles(root, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(30))
        {
            if (new FileInfo(file).Length > 8 * 1024 * 1024) continue;
            PlayFiles.NoLinksToRoot(file);
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(file, ct));
            result.Add(json.RootElement.Clone());
        }
        return result;
    }
    private async Task InspectCoreAsync(string id, CancellationToken ct)
    {
        var fetched = await FetchAsync(id, ct);
        var server = await RequireServerAsync(id, ct);
        var java = await _runtimes.FindAsync(RuntimeCatalog.Java(fetched.Manifest.Java), "java", ct);
        var prism = await _runtimes.FindAsync(RuntimeCatalog.Prism, "prism", ct);
        var engine = Engine(Connect(server));
        var javaPath = java?.Executable ?? PlayFiles.Child(_paths.Runtimes, "java/pending/bin/java.exe");
        var plan = await engine.InspectAsync(id, fetched.Manifest, javaPath, server.MemoryMiB, PlaySettingsStore.Selected(fetched.Manifest, server), ct: ct);
        if (java is not null) await RuntimeManager.InspectJavaAsync(java.Executable, fetched.Manifest.Java.Major, ct);
        Set(id, Current(id) with { JavaReady = java is not null, PrismReady = prism is not null, Plan = plan, Directory = _paths.Game(id),
            Stage = java is null || prism is null ? "setup" : plan.Changes.Count > 0 || plan.UnknownMods.Count > 0 ? "update" : server.GameObserved ? "ready" : "prismSetup" });
    }
    private async Task<FetchedRelease> FetchAsync(string id, CancellationToken ct)
    {
        Set(id, Current(id) with { Stage = "checking", Error = null, ErrorCode = null, Received = 0, Total = 0, CurrentFile = null });
        var server = await RequireServerAsync(id, ct);
        if (server.PlayerName is null) throw new DistributionException("name_required", "Minecraftの名前を入力してください。");
        var connection = Connect(server);
        if (connection.Client.PlayerName != server.PlayerName) await connection.Client.IdentifyAsync(server.PlayerName, ct);
        var fetched = await connection.Client.FetchAsync(Math.Max(server.HighestSequence, 1), ct);
        await SaveTrustAsync(id, connection.Client, fetched.Manifest.Sequence, fetched.Manifest.ServerName, ct);
        Set(id, Current(id) with { Manifest = fetched.Manifest, Status = fetched.Status });
        return fetched;
    }
    private async Task LaunchCoreAsync(string id, RuntimeInstallation prism, CancellationToken ct)
    {
        var server = await RequireServerAsync(id, ct);
        var connection = Connect(server);
        var before = Current(id).Manifest!;
        var current = await connection.Client.FetchAsync(Math.Max(server.HighestSequence, 1), ct);
        await SaveTrustAsync(id, connection.Client, current.Manifest.Sequence, current.Manifest.ServerName, ct);
        if (current.Manifest.ReleaseId != before.ReleaseId) throw new DistributionException("release_changed", "配布内容が更新されました。もう一度準備してください。", true);
        var allowed = await connection.Client.CheckLaunchAsync(before.ReleaseId, ct);
        if (!allowed.Allowed) throw new DistributionException(allowed.Reason ?? "launch_not_ready",
            allowed.Reason == "server_offline" ? "サーバーは停止中です。環境の準備は完了しています。" : "配布内容とサーバー構成の対応を確認できません。");
        var activity = _activity.Read();
        if (activity.Uncertain || activity.GameRunning) throw new DistributionException("game_running", "起動中のゲームを確認してください。");
        _launcher.Start(PrismProfile.Launch(prism.Executable, _paths, id, before.GameEndpoint));
        _launchedServer = id;
        Set(id, Current(id) with { Stage = "launching" });
    }
    private IProgress<TransferProgress> Transfer(string id) => new CallbackProgress<TransferProgress>(progress =>
        Set(id, Current(id) with { Received = progress.Received, Total = progress.Total, CurrentFile = progress.FileId }));
    private SyncEngine Engine(Connection connection) => new(_paths, _activity, new ClientFileProvider(connection.Client, connection.Downloads, _paths, _reuse.ToArray(), TransferServer(connection)));
    private IProgress<TransferProgress> TransferServer(Connection connection)
    {
        var id = _connections.First(pair => ReferenceEquals(pair.Value, connection)).Key;
        return Transfer(id);
    }
    private Connection Connect(SavedServer server)
    {
        if (_connections.TryGetValue(server.Id, out var existing))
        {
            if (existing.Client.TrustedKey == server.SigningKey && existing.Client.KeyMinimumSequence >= server.KeyMinimumSequence) return existing;
            if (_connections.TryRemove(server.Id, out var removed)) removed.Dispose();
        }
        return _connections.GetOrAdd(server.Id, _ => new Connection(server, _development));
    }
    private Task<PlaySettings> SaveTrustAsync(string id, DistributionClient client, long sequence, string name, CancellationToken ct) =>
        UpdateServerAsync(id, value =>
        {
            if (value.SigningKey != client.TrustedKey && client.KeyMinimumSequence <= value.KeyMinimumSequence)
                throw new DistributionException("signing_key_changed", "確認中に署名鍵が更新されました。もう一度確認してください。");
            return value with { HighestSequence = Math.Max(value.HighestSequence, sequence), Name = name, SigningKey = client.TrustedKey!,
                KeyMinimumSequence = Math.Max(value.KeyMinimumSequence, client.KeyMinimumSequence) };
        }, ct);
    private ServerView Current(string id) => _views.GetValueOrDefault(id) ?? new(id);
    private void Set(string id, ServerView value) { _views[id] = value; Changed?.Invoke(); }
    private async Task<SavedServer> RequireServerAsync(string id, CancellationToken ct) => (await _settings.ReadAsync(ct)).Servers!.FirstOrDefault(server => server.Id == id)
        ?? throw new DistributionException("server_not_found", "登録したサーバーが見つかりません。");
    private Task<PlaySettings> UpdateServerAsync(string id, Func<SavedServer, SavedServer> update, CancellationToken ct) =>
        _settings.UpdateAsync(settings => settings with { Servers = settings.Servers!.Select(server => server.Id == id ? update(server) : server).ToArray() }, ct);
    private async Task RunAsync(string id, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        if (!await _operation.WaitAsync(0, ct)) throw new DistributionException("operation_busy", "別の処理が実行中です。");
        using var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _active = source; _busy = true; _canCancel = true; Changed?.Invoke();
        try { await action(source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { Set(id, Current(id) with { Stage = "cancelled", Error = "処理を中断しました。次回の準備時に状態を確認します。", ErrorCode = "cancelled" }); }
        catch (Exception error)
        {
            var domain = error as DistributionException;
            var message = domain?.Message ?? (error is HttpRequestException or OperationCanceledException ? "配布サーバーに接続できません。通信を確認してください。" : "処理を完了できませんでした。診断ログで確認してください。");
            Set(id, Current(id) with { Stage = "error", Error = message, ErrorCode = domain?.Code ?? "operation_failed" });
            throw new DistributionException(domain?.Code ?? "operation_failed", message, domain?.Retryable ?? false);
        }
        finally { _active = null; _busy = false; _canCancel = false; _operation.Release(); Changed?.Invoke(); }
    }
    public void Dispose()
    {
        _active?.Cancel();
        foreach (var connection in _connections.Values) connection.Dispose();
        _runtimeHttp.Dispose();
    }
    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    { public void Report(T value) => callback(value); }
}
