using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record SavedServer(string Id, DistributionTarget Target, string Name, DistributionKey SigningKey,
    string? PlayerName = null, long HighestSequence = 0, int MemoryMiB = 4096,
    IReadOnlyDictionary<string, bool>? OptionalChoices = null, bool GameObserved = false, long KeyMinimumSequence = 0);
public sealed record PlaySettings(int SchemaVersion = 1, string Theme = "system", string? SelectedServer = null,
    IReadOnlyList<SavedServer>? Servers = null);
public sealed class PlaySettingsStore(PlayPaths paths, bool development = false)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string FilePath => PlayFiles.Child(paths.Root, "settings.json");
    public async Task<PlaySettings> ReadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadCoreAsync(ct); }
        finally { _gate.Release(); }
    }
    private async Task<PlaySettings> ReadCoreAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath))
        {
            if (File.Exists(PlayFiles.Child(paths.Root, "settings.previous.json")))
                throw new DistributionException("settings_corrupt", "アプリ設定が見つかりません。保存済みの設定から復元できます。");
            return new(Servers: []);
        }
        return await ReadDocumentAsync(FilePath, ct);
    }
    private async Task<PlaySettings> ReadDocumentAsync(string file, CancellationToken ct)
    {
        if (new FileInfo(file).Length > 1024 * 1024) throw new DistributionException("settings_corrupt", "アプリ設定が大きすぎます。");
        try
        {
            var settings = DistributionJson.Read<PlaySettings>(await File.ReadAllBytesAsync(file, ct));
            Validate(settings);
            return settings with { Servers = settings.Servers ?? [] };
        }
        catch (Exception error) when (error is System.Text.Json.JsonException || error is DistributionException domain && domain.Code != "settings_version")
        { throw new DistributionException("settings_corrupt", "アプリ設定を読み取れません。保存済みの設定から復元できます。"); }
    }
    public async Task<PlaySettings> RestorePreviousAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(FilePath))
            {
                var corrupt = false;
                try { await ReadCoreAsync(ct); }
                catch (DistributionException error) when (error.Code == "settings_corrupt") { corrupt = true; }
                if (!corrupt) throw new DistributionException("settings_valid", "現在の設定は正常です。復元は必要ありません。");
            }
            var previous = PlayFiles.Child(paths.Root, "settings.previous.json");
            if (!File.Exists(previous)) throw new DistributionException("settings_recovery_unavailable", "復元できる設定がありません。設定ファイルを確認してください。");
            var restored = await ReadDocumentAsync(previous, ct);
            var original = PlayFiles.Child(paths.Root, "settings.corrupt-" + Guid.NewGuid().ToString("N") + ".json");
            if (File.Exists(FilePath)) File.Move(FilePath, original);
            try { await PlayFiles.WriteAtomicAsync(FilePath, DistributionJson.Bytes(restored), ct); }
            catch
            {
                if (File.Exists(original) && !File.Exists(FilePath)) File.Move(original, FilePath);
                throw;
            }
            return restored;
        }
        finally { _gate.Release(); }
    }
    public async Task<PlaySettings> UpdateAsync(Func<PlaySettings, PlaySettings> update, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await ReadCoreAsync(ct);
            var changed = update(current);
            Validate(changed);
            var bytes = DistributionJson.Bytes(changed);
            if (bytes.Length > 1024 * 1024) throw new DistributionException("settings_size", "保存できる設定のサイズを超えています。");
            if (File.Exists(FilePath))
            {
                var recovery = current with { Servers = current.Servers!.Select(saved =>
                {
                    var next = changed.Servers?.FirstOrDefault(server => server.Id == saved.Id);
                    return next is null ? saved : saved with { HighestSequence = Math.Max(saved.HighestSequence, next.HighestSequence), SigningKey = next.SigningKey, KeyMinimumSequence = Math.Max(saved.KeyMinimumSequence, next.KeyMinimumSequence) };
                }).ToArray() };
                await PlayFiles.WriteAtomicAsync(PlayFiles.Child(paths.Root, "settings.previous.json"), DistributionJson.Bytes(recovery), ct);
            }
            await PlayFiles.WriteAtomicAsync(FilePath, bytes, ct);
            return changed;
        }
        finally { _gate.Release(); }
    }
    private void Validate(PlaySettings settings)
    {
        if (settings.SchemaVersion != 1) throw new DistributionException("settings_version", "この設定を読むには対応するバージョンのmcmere Playが必要です。");
        if (settings.Theme is not ("system" or "light" or "dark") || settings.Servers?.Count > 64)
            throw new DistributionException("settings_corrupt", "対応しないアプリ設定です。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in settings.Servers ?? [])
        {
            if (server is null || server.Target is null || server.SigningKey is null) throw new DistributionException("settings_corrupt", "サーバー設定が不正です。");
            var target = DistributionTarget.Parse(server.Target.Origin + "/s/" + server.Target.PublicId, development);
            if (target != server.Target || server.Id != paths.InstanceId(target.BaseUri, target.PublicId) || !ids.Add(server.Id) ||
                server.Name is not { Length: >= 1 and <= 100 } || server.MemoryMiB is < 1024 or > 32768 || server.MemoryMiB % 64 != 0 || server.HighestSequence < 0 ||
                server.KeyMinimumSequence < 0 || server.OptionalChoices?.Count > 4096) throw new DistributionException("settings_corrupt", "サーバー設定が不正です。");
            ManifestValidation.Hash(server.SigningKey.KeyId, 64);
            try
            {
                if (server.SigningKey.PublicKey is not { Length: > 0 and <= 16384 }) throw new FormatException();
                using var key = System.Security.Cryptography.RSA.Create();
                var bytes = Convert.FromBase64String(server.SigningKey.PublicKey);
                key.ImportSubjectPublicKeyInfo(bytes, out var consumed);
                if (consumed != bytes.Length || key.KeySize < 2048 || ManifestSigning.PublicKey(key) != server.SigningKey) throw new FormatException();
            }
            catch (Exception error) when (error is FormatException or System.Security.Cryptography.CryptographicException)
            { throw new DistributionException("settings_corrupt", "公開鍵が不正です。"); }
            if (server.PlayerName is not null) ManifestValidation.Name(server.PlayerName);
        }
        if (settings.SelectedServer is not null && !ids.Contains(settings.SelectedServer)) throw new DistributionException("settings_corrupt", "選択したサーバーが見つかりません。");
    }
    public static string ChoiceKey(PackFile file) => file.Source.ProjectId is { Length: > 0 } project ? "project:" + project :
        file.ModIds.Count > 0 ? "mods:" + string.Join(",", file.ModIds.Order(StringComparer.Ordinal)) : "path:" + file.Path;
    public static HashSet<string> Selected(PackManifest manifest, SavedServer server) => manifest.Files
        .Where(file => file.Requirement == FileRequirement.Recommended &&
            (server.OptionalChoices?.TryGetValue(ChoiceKey(file), out var enabled) == true ? enabled : file.DefaultEnabled))
        .Select(file => file.Id).ToHashSet(StringComparer.Ordinal);
}
