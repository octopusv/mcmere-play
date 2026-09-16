using System.Security.Cryptography;
using System.Text.Json;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public enum SyncAction { Add, Replace, Remove, Quarantine }
public sealed record SyncItem(string Scope, string Path, SyncAction Action, string? BeforeHash, long BeforeLength,
    string? AfterHash, long AfterLength, string? FileId, PackOptionChange? Options = null);
public sealed record SyncPlan(string Id, string InstanceId, string ReleaseId, IReadOnlyList<SyncItem> Changes,
    IReadOnlyList<string> UnknownMods, long RequiredFreeBytes, IReadOnlyList<string> SelectedIds);
public sealed record SyncProgress(string Stage, int Completed, int Total);
public sealed record AppliedPack(string TransactionId, PackManifest Manifest, IReadOnlyList<string> SelectedIds, DateTimeOffset AppliedAt);
internal sealed record SyncJournal(string Id, string InstanceId, string Phase, IReadOnlyList<SyncItem> Operations);
public interface IInstanceActivity { Task RequireIdleAsync(string instanceId, CancellationToken ct); }
public interface IFileProvider { Task<string> GetAsync(PackFile file, CancellationToken ct); }
public interface IAvailableSpace { long Bytes(string directory); }
public sealed class AvailableSpace : IAvailableSpace
{
    public long Bytes(string directory) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
}

public sealed class SyncEngine(PlayPaths paths, IInstanceActivity activity, IFileProvider provider, IAvailableSpace? capacity = null)
{
    private readonly IAvailableSpace _capacity = capacity ?? new AvailableSpace();
    private string StateRoot(string id) => PlayFiles.Child(paths.State, ValidateInstance(id));
    private static string ValidateInstance(string id)
    {
        ManifestValidation.RelativePath(id);
        if (id.Contains('/')) throw new DistributionException("invalid_instance", "インスタンスのIDが不正です。");
        return id;
    }
    private string Resolve(string instanceId, SyncItem operation) => operation.Scope switch
    {
        "game" => PlayFiles.Child(paths.Game(instanceId), operation.Path),
        "profile" when operation.Path is "instance.cfg" or "mmc-pack.json" => PlayFiles.Child(paths.Instance(instanceId), operation.Path),
        "resourcePackOptions" when operation.Path == "options.txt" => PlayFiles.Child(paths.Game(instanceId), "options.txt"),
        _ => throw new DistributionException("invalid_journal", "更新記録の対象が不正です。")
    };

    public async Task<AppliedPack?> AppliedAsync(string instanceId, CancellationToken ct = default)
    {
        var file = PlayFiles.Child(StateRoot(instanceId), "applied.json");
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 8 * 1024 * 1024) throw new DistributionException("invalid_state", "保存した構成情報が大きすぎます。");
        try
        {
            var applied = DistributionJson.Read<AppliedPack>(await File.ReadAllBytesAsync(file, ct));
            ManifestValidation.Id(applied.TransactionId); ManifestValidation.Validate(applied.Manifest);
            if (applied.SelectedIds is null || applied.SelectedIds.Count != applied.SelectedIds.Distinct(StringComparer.Ordinal).Count() ||
                applied.SelectedIds.Any(id => !applied.Manifest.Files.Any(candidate => candidate.Id == id)))
                throw new DistributionException("invalid_state", "保存した構成の選択情報が不正です。");
            return applied;
        }
        catch (Exception error) when (error is JsonException or NullReferenceException)
        { throw new DistributionException("invalid_state", "保存した構成情報を読み取れません。"); }
    }

    public async Task<SyncPlan> PlanAsync(string instanceId, PackManifest manifest, string javaPath, int memoryMiB,
        ISet<string>? optionalIds = null, bool quarantineUnknown = false, CancellationToken ct = default)
    {
        await using var gate = Lock(instanceId);
        await activity.RequireIdleAsync(instanceId, ct);
        await RecoverCoreAsync(instanceId, ct);
        return (await PlanCoreAsync(instanceId, manifest, javaPath, memoryMiB, optionalIds, quarantineUnknown, ct)).Plan;
    }

    public async Task<SyncPlan> InspectAsync(string instanceId, PackManifest manifest, string javaPath, int memoryMiB,
        ISet<string>? optionalIds = null, bool quarantineUnknown = false, CancellationToken ct = default)
    {
        await using var gate = Lock(instanceId);
        if (File.Exists(PlayFiles.Child(StateRoot(instanceId), "journal.json")))
            throw new DistributionException("recovery_required", "前回の更新を復旧してから確認してください。");
        return (await PlanCoreAsync(instanceId, manifest, javaPath, memoryMiB, optionalIds, quarantineUnknown, ct)).Plan;
    }

    private async Task<(SyncPlan Plan, IReadOnlyDictionary<string, byte[]> Profiles)> PlanCoreAsync(string instanceId, PackManifest manifest,
        string javaPath, int memoryMiB, ISet<string>? optionalIds, bool quarantineUnknown, CancellationToken ct)
    {
        var selected = ManifestValidation.Selected(manifest, optionalIds);
        var previous = await AppliedAsync(instanceId, ct);
        if (previous is not null && (previous.Manifest.ServerPublicId != manifest.ServerPublicId || previous.Manifest.Sequence > manifest.Sequence))
            throw new DistributionException("release_outdated", "保存済みの構成と配布先または版が一致しません。");
        var oldFiles = previous?.Manifest.Files.Where(file => previous.SelectedIds.Contains(file.Id)).ToArray() ?? [];
        var changes = new List<SyncItem>();
        foreach (var file in selected)
        {
            var path = PlayFiles.Child(paths.Game(instanceId), file.Path);
            if (file.UpdatePolicy == FileUpdatePolicy.Seed && File.Exists(path)) continue;
            var before = await SnapshotAsync(path, ct);
            if (manifest.ResourcePacks.Any(item => item.FileId == file.Id) && before.Hash is not null)
            {
                if (before.Hash != file.Sha512 && !oldFiles.Any(old => old.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase)))
                    throw new DistributionException("resource_pack_conflict", "配置先に未管理のリソースパックがあります。フォルダーで確認してください。");
                if (before.Hash == file.Sha512) PackArchive.RequireCompatible(await PackArchive.InspectAsync(path, ct), manifest.MinecraftVersion, false, true);
            }
            if (before.Hash != file.Sha512)
                changes.Add(new("game", file.Path, before.Hash is null ? SyncAction.Add : SyncAction.Replace, before.Hash, before.Length, file.Sha512, file.Length, file.Id));
        }
        foreach (var old in oldFiles.Where(old => old.UpdatePolicy == FileUpdatePolicy.Managed && !selected.Any(file => file.Path.Equals(old.Path, StringComparison.OrdinalIgnoreCase))))
        {
            var before = await SnapshotAsync(PlayFiles.Child(paths.Game(instanceId), old.Path), ct);
            if (before.Hash is not null) changes.Add(new("game", old.Path, SyncAction.Remove, before.Hash, before.Length, null, 0, null));
        }
        var known = selected.Select(file => file.Path).Concat(oldFiles.Select(file => file.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (var path in PlayFiles.Files(PlayFiles.Child(paths.Game(instanceId), "mods")).Where(file => file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
        {
            var relative = Path.GetRelativePath(paths.Game(instanceId), path).Replace('\\', '/');
            if (known.Contains(relative)) continue;
            unknown.Add(relative);
            if (quarantineUnknown)
            {
                var before = await SnapshotAsync(path, ct);
                changes.Add(new("game", relative, SyncAction.Quarantine, before.Hash, before.Length, null, 0, null));
            }
        }
        var cfgPath = PlayFiles.Child(paths.Instance(instanceId), "instance.cfg");
        var packPath = PlayFiles.Child(paths.Instance(instanceId), "mmc-pack.json");
        if (File.Exists(cfgPath) && new FileInfo(cfgPath).Length > 65536 || File.Exists(packPath) && new FileInfo(packPath).Length > 1024 * 1024)
            throw new DistributionException("invalid_profile", "Prismの構成ファイルが大きすぎます。");
        var existing = File.Exists(cfgPath) ? await File.ReadAllTextAsync(cfgPath, ct) : null;
        var existingPack = File.Exists(packPath) ? await File.ReadAllTextAsync(packPath, ct) : null;
        var profiles = PrismProfile.Prepare(manifest, javaPath, memoryMiB, existing, existingPack);
        foreach (var entry in profiles)
        {
            var before = await SnapshotAsync(PlayFiles.Child(paths.Instance(instanceId), entry.Key), ct);
            var hash = Convert.ToHexString(SHA512.HashData(entry.Value)).ToLowerInvariant();
            if (before.Hash != hash) changes.Add(new("profile", entry.Key, before.Hash is null ? SyncAction.Add : SyncAction.Replace, before.Hash, before.Length, hash, entry.Value.Length, null));
        }
        var generated = profiles.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        if (manifest.SchemaVersion == 2 || previous?.Manifest.SchemaVersion == 2)
        {
            var path = PlayFiles.Child(paths.Game(instanceId), "options.txt");
            var before = await SnapshotAsync(path, ct);
            if (before.Length > 2 * 1024 * 1024) throw new DistributionException("options_conflict", "options.txtのサイズが上限を超えています。");
            var bytes = before.Hash is null ? [] : await File.ReadAllBytesAsync(path, ct);
            var patch = ResourcePackOptions.Plan(bytes, manifest, previous?.Manifest, selected);
            if (patch.Before != patch.After)
            {
                var next = ResourcePackOptions.Write(bytes, patch.After);
                generated["options.txt"] = next;
                changes.Add(new("resourcePackOptions", "options.txt", before.Hash is null ? SyncAction.Add : SyncAction.Replace,
                    before.Hash, before.Length, Convert.ToHexString(SHA512.HashData(next)).ToLowerInvariant(), next.Length, null, patch));
            }
        }
        var ids = selected.Select(file => file.Id).Order(StringComparer.Ordinal).ToArray();
        var id = Convert.ToHexString(SHA256.HashData(DistributionJson.Bytes(new { manifest.ReleaseId, manifest.Sequence, Changes = changes, Selected = ids, unknown }))).ToLowerInvariant();
        var space = checked(changes.Sum(item => checked(item.AfterLength * 2 + item.BeforeLength)) + (changes.Count > 0 ? 256L * 1024 * 1024 : 8L * 1024 * 1024));
        return (new(id, instanceId, manifest.ReleaseId, changes, unknown, space, ids), generated);
    }

    public async Task<AppliedPack> SynchronizeAsync(string instanceId, PackManifest manifest, string javaPath, int memoryMiB,
        ISet<string>? optionalIds = null, bool quarantineUnknown = false, string? expectedPlanId = null,
        IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        await using var gate = Lock(instanceId);
        await activity.RequireIdleAsync(instanceId, ct);
        await RecoverCoreAsync(instanceId, ct);
        var prepared = await PlanCoreAsync(instanceId, manifest, javaPath, memoryMiB, optionalIds, quarantineUnknown, ct);
        var plan = prepared.Plan;
        if (expectedPlanId is not null && expectedPlanId != plan.Id) throw new DistributionException("plan_changed", "手元の環境が変わりました。変更内容を確認し直してください。", true);
        if (plan.Changes.Count == 0 && await AppliedAsync(instanceId, ct) is { } current && current.Manifest.ReleaseId == manifest.ReleaseId && current.SelectedIds.SequenceEqual(plan.SelectedIds))
        {
            progress?.Report(new("complete", 0, 0));
            return current;
        }
        if (_capacity.Bytes(paths.Root) < plan.RequiredFreeBytes) throw new DistributionException("disk_space", "更新と退避に必要な空き容量がありません。");
        var tx = Guid.NewGuid().ToString("N");
        var staging = PlayFiles.Child(paths.Staging, tx);
        var backup = PlayFiles.Child(paths.Backups, instanceId + "/" + tx);
        var journalPath = PlayFiles.Child(StateRoot(instanceId), "journal.json");
        var journal = new SyncJournal(tx, instanceId, "preparing", plan.Changes);
        await WriteJournalAsync(journalPath, journal, ct);
        try
        {
            var completed = 0;
            using var parallel = new SemaphoreSlim(3);
            await Task.WhenAll(plan.Changes.Where(item => item.AfterHash is not null).Select(async operation =>
            {
                await parallel.WaitAsync(ct);
                try
                {
                    var target = PlayFiles.Child(staging, operation.Scope + "/" + operation.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (operation.Scope is "profile" or "resourcePackOptions") await File.WriteAllBytesAsync(target, prepared.Profiles[operation.Path], ct);
                    else
                    {
                        var file = manifest.Files.Single(file => file.Id == operation.FileId);
                        var source = await provider.GetAsync(file, ct);
                        PlayFiles.NoLinksToRoot(source);
                        await CopyAsync(source, target, ct);
                    }
                    if (new FileInfo(target).Length != operation.AfterLength || await PlayFiles.Sha512Async(target, ct) != operation.AfterHash)
                        throw new DistributionException("download_corrupt", "準備したファイルのハッシュが一致しません。");
                    if (manifest.ResourcePacks.Any(item => item.FileId == operation.FileId))
                        PackArchive.RequireCompatible(await PackArchive.InspectAsync(target, ct), manifest.MinecraftVersion, false, true);
                    progress?.Report(new("preparing", Interlocked.Increment(ref completed), plan.Changes.Count));
                }
                finally { parallel.Release(); }
            }));
            await activity.RequireIdleAsync(instanceId, ct);
            foreach (var operation in plan.Changes)
            {
                await RequireBeforeAsync(instanceId, operation, ct);
                if (operation.BeforeHash is null) continue;
                var saved = PlayFiles.Child(backup, operation.Scope + "/" + operation.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                await CopyAsync(Resolve(instanceId, operation), saved, ct);
                if (await PlayFiles.Sha512Async(saved, ct) != operation.BeforeHash) throw new DistributionException("backup_changed", "退避したファイルを確認できません。");
            }
            journal = journal with { Phase = "applying" };
            await WriteJournalAsync(journalPath, journal, ct);
            completed = 0;
            foreach (var operation in plan.Changes)
            {
                ct.ThrowIfCancellationRequested();
                await activity.RequireIdleAsync(instanceId, ct);
                await RequireBeforeAsync(instanceId, operation, ct);
                var destination = Resolve(instanceId, operation);
                if (operation.AfterHash is null) File.Delete(destination);
                else await ReplaceAsync(PlayFiles.Child(staging, operation.Scope + "/" + operation.Path), destination, ct);
                progress?.Report(new("applying", ++completed, plan.Changes.Count));
            }
            ct.ThrowIfCancellationRequested();
            var applied = new AppliedPack(tx, manifest, plan.SelectedIds, DateTimeOffset.UtcNow);
            await PlayFiles.WriteAtomicAsync(PlayFiles.Child(StateRoot(instanceId), "applied.json"), DistributionJson.Bytes(applied), ct);
            await FinishAsync(journal, applied, CancellationToken.None);
            progress?.Report(new("complete", plan.Changes.Count, plan.Changes.Count));
            return applied;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            await activity.RequireIdleAsync(instanceId, CancellationToken.None);
            await RecoverCoreAsync(instanceId, CancellationToken.None);
            throw;
        }
    }

    public async Task RecoverAsync(string instanceId, CancellationToken ct = default)
    {
        await using var gate = Lock(instanceId);
        await activity.RequireIdleAsync(instanceId, ct);
        await RecoverCoreAsync(instanceId, ct);
    }

    private async Task RecoverCoreAsync(string instanceId, CancellationToken ct)
    {
        var path = PlayFiles.Child(StateRoot(instanceId), "journal.json");
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new DistributionException("invalid_journal", "更新記録が大きすぎます。");
        SyncJournal journal;
        try { journal = DistributionJson.Read<SyncJournal>(await File.ReadAllBytesAsync(path, ct)); }
        catch (JsonException) { throw new DistributionException("invalid_journal", "更新記録を読み取れません。"); }
        ValidateJournal(journal, instanceId);
        var applied = await AppliedAsync(instanceId, ct);
        if (applied?.TransactionId == journal.Id) { await FinishAsync(journal, applied, ct); return; }
        if (journal.Phase == "applying")
        {
            var backup = PlayFiles.Child(paths.Backups, instanceId + "/" + journal.Id);
            foreach (var operation in journal.Operations.Reverse())
            {
                var destination = Resolve(instanceId, operation);
                if (operation.Options is { } options)
                {
                    if (File.Exists(destination) && new FileInfo(destination).Length > 2 * 1024 * 1024)
                        throw new DistributionException("options_conflict", "個人設定が大きすぎるため復旧を保留しました。");
                    var bytes = File.Exists(destination) ? await File.ReadAllBytesAsync(destination, ct) : [];
                    var restored = ResourcePackOptions.Restore(bytes, options);
                    if (restored.Length == 0 && operation.BeforeHash is null) { if (File.Exists(destination)) File.Delete(destination); }
                    else await PlayFiles.WriteAtomicAsync(destination, restored, ct);
                    continue;
                }
                var current = await SnapshotAsync(destination, ct);
                if (current.Hash == operation.BeforeHash) continue;
                if (current.Hash != operation.AfterHash) throw new DistributionException("recovery_conflict", "更新後に変更されたファイルがあります。自動復旧を保留しました。");
                if (operation.BeforeHash is null) { if (File.Exists(destination)) File.Delete(destination); continue; }
                var saved = PlayFiles.Child(backup, operation.Scope + "/" + operation.Path);
                if (!File.Exists(saved) || await PlayFiles.Sha512Async(saved, ct) != operation.BeforeHash)
                    throw new DistributionException("backup_corrupt", "退避したファイルを確認できないため復旧を保留しました。");
                await ReplaceAsync(saved, destination, ct);
            }
        }
        File.Delete(path);
        DeleteTree(paths.Staging, journal.Id);
        DeleteTree(paths.Backups, instanceId + "/" + journal.Id);
    }

    private async Task FinishAsync(SyncJournal journal, AppliedPack applied, CancellationToken ct)
    {
        var history = PlayFiles.Child(StateRoot(journal.InstanceId), "history/" + journal.Id + ".json");
        await PlayFiles.WriteAtomicAsync(history, DistributionJson.Bytes(new { journal.Id, applied.Manifest.ReleaseId, applied.AppliedAt, journal.Operations }), ct);
        if (journal.Operations.Count == 0)
        {
            File.Delete(PlayFiles.Child(StateRoot(journal.InstanceId), "journal.json"));
            DeleteTree(paths.Staging, journal.Id);
            return;
        }
        var backupRoot = PlayFiles.Child(paths.Backups, journal.InstanceId);
        var marker = PlayFiles.Child(backupRoot, journal.Id + "/complete.json");
        await PlayFiles.WriteAtomicAsync(marker, DistributionJson.Bytes(applied.AppliedAt), ct);
        File.Delete(PlayFiles.Child(StateRoot(journal.InstanceId), "journal.json"));
        DeleteTree(paths.Staging, journal.Id);
        var completed = new List<(string Id, DateTimeOffset At)>();
        foreach (var directory in Directory.EnumerateDirectories(backupRoot))
        {
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            var complete = PlayFiles.Child(directory, "complete.json");
            if (!File.Exists(complete)) continue;
            try { completed.Add((id, DistributionJson.Read<DateTimeOffset>(await File.ReadAllBytesAsync(complete, ct)))); }
            catch (JsonException) { }
        }
        foreach (var old in completed.OrderByDescending(item => item.At).Skip(2)) DeleteTree(backupRoot, old.Id);
    }

    private static void ValidateJournal(SyncJournal journal, string instanceId)
    {
        if (journal is null || journal.InstanceId != instanceId || journal.Phase is not ("preparing" or "applying") || journal.Operations is null || journal.Operations.Count > 10000)
            throw new DistributionException("invalid_journal", "更新記録の形式が不正です。");
        ManifestValidation.Id(journal.Id);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in journal.Operations)
        {
            ManifestValidation.RelativePath(operation.Path);
            if (operation.Scope == "game" && operation.Path.Split('/')[0] is not ("mods" or "config" or "defaultconfigs" or "resourcepacks" or "shaderpacks") ||
                operation.Scope == "profile" && operation.Path is not ("instance.cfg" or "mmc-pack.json") ||
                operation.Scope == "resourcePackOptions" && (operation.Path != "options.txt" || operation.Options is null) ||
                operation.Scope is not ("game" or "profile" or "resourcePackOptions") ||
                operation.Scope != "resourcePackOptions" && operation.Options is not null ||
                !targets.Add(operation.Scope + "/" + operation.Path)) throw new DistributionException("invalid_journal", "更新記録の保存先が不正です。");
            if (operation.Options is not null) ResourcePackOptions.Validate(operation.Options);
            if (operation.BeforeHash is not null) ManifestValidation.Hash(operation.BeforeHash, 128);
            if (operation.AfterHash is not null) ManifestValidation.Hash(operation.AfterHash, 128);
            if (operation.BeforeHash is null && operation.AfterHash is null) throw new DistributionException("invalid_journal", "更新記録にファイル情報がありません。");
        }
    }
    private FileStream Lock(string instanceId)
    {
        var root = StateRoot(instanceId); Directory.CreateDirectory(root);
        try { return new FileStream(PlayFiles.Child(root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DistributionException("instance_busy", "この環境を別の処理が使用しています。", true); }
    }
    private async Task RequireBeforeAsync(string instanceId, SyncItem operation, CancellationToken ct)
    {
        var snapshot = await SnapshotAsync(Resolve(instanceId, operation), ct);
        if (snapshot.Hash != operation.BeforeHash) throw new DistributionException("local_files_changed", "準備中に手元のファイルが変更されました。", true);
    }
    private static async Task<(string? Hash, long Length)> SnapshotAsync(string path, CancellationToken ct)
    {
        PlayFiles.NoLinksToRoot(path);
        return File.Exists(path) ? (await PlayFiles.Sha512Async(path, ct), new FileInfo(path).Length) : (null, 0);
    }
    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        PlayFiles.NoLinksToRoot(source); PlayFiles.NoLinksToRoot(destination);
        await using var input = File.OpenRead(source);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, ct);
        output.Flush(true);
    }
    private static async Task ReplaceAsync(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await CopyAsync(source, temp, ct);
            PlayFiles.NoLinksToRoot(destination);
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static Task WriteJournalAsync(string path, SyncJournal journal, CancellationToken ct) => PlayFiles.WriteAtomicAsync(path, DistributionJson.Bytes(journal), ct);
    private static void DeleteTree(string root, string relative)
    {
        var directory = PlayFiles.Child(root, relative);
        if (!Directory.Exists(directory)) return;
        _ = PlayFiles.Files(directory).ToArray();
        Directory.Delete(directory, true);
    }
}
