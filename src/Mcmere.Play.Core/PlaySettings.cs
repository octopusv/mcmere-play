using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record SavedServer(string Id, DistributionTarget Target, string Name, DistributionKey SigningKey,
    string? PlayerName = null, long HighestSequence = 0, int MemoryMiB = 4096,
    IReadOnlyDictionary<string, bool>? OptionalChoices = null, bool GameObserved = false);
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
        if (!File.Exists(FilePath)) return new(Servers: []);
        if (new FileInfo(FilePath).Length > 1024 * 1024) throw new DistributionException("settings_corrupt", "アプリ設定が大きすぎます。");
        PlaySettings settings;
        try { settings = DistributionJson.Read<PlaySettings>(await File.ReadAllBytesAsync(FilePath, ct)); }
        catch (System.Text.Json.JsonException) { throw new DistributionException("settings_corrupt", "アプリ設定を読み取れません。"); }
        Validate(settings);
        return settings with { Servers = settings.Servers ?? [] };
    }
    public async Task<PlaySettings> UpdateAsync(Func<PlaySettings, PlaySettings> update, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await ReadCoreAsync(ct);
            var changed = update(current);
            Validate(changed);
            if (File.Exists(FilePath)) await PlayFiles.WriteAtomicAsync(PlayFiles.Child(paths.Root, "settings.previous.json"), DistributionJson.Bytes(current), ct);
            await PlayFiles.WriteAtomicAsync(FilePath, DistributionJson.Bytes(changed), ct);
            return changed;
        }
        finally { _gate.Release(); }
    }
    private void Validate(PlaySettings settings)
    {
        if (settings.SchemaVersion != 1 || settings.Theme is not ("system" or "light" or "dark") || settings.Servers?.Count > 64)
            throw new DistributionException("settings_corrupt", "対応しないアプリ設定です。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in settings.Servers ?? [])
        {
            if (server is null || server.Target is null || server.SigningKey is null) throw new DistributionException("settings_corrupt", "サーバー設定が不正です。");
            var target = DistributionTarget.Parse(server.Target.Origin + "/s/" + server.Target.PublicId, development);
            if (target != server.Target || server.Id != paths.InstanceId(target.BaseUri, target.PublicId) || !ids.Add(server.Id) ||
                server.Name.Length is < 1 or > 100 || server.MemoryMiB is < 1024 or > 32768 || server.MemoryMiB % 64 != 0 || server.HighestSequence < 0 ||
                server.OptionalChoices?.Count > 4096) throw new DistributionException("settings_corrupt", "サーバー設定が不正です。");
            ManifestValidation.Hash(server.SigningKey.KeyId, 64);
            if (server.SigningKey.PublicKey.Length > 16384) throw new DistributionException("settings_corrupt", "公開鍵が不正です。");
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
