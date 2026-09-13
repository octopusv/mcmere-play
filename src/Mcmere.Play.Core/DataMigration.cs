using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record MigrationPlan(string Id, string Source, string Destination, long Bytes, long RequiredFreeBytes, int Files, bool PrismLoginRequired = true);
public sealed record MigrationProgress(string Stage, long CopiedBytes, long TotalBytes, int CompletedFiles, int TotalFiles);
internal sealed record MigrationFile(string Path, long Length, long ModifiedTicks);
internal sealed record MigrationStage(string Id, string PlanId, string ControlRoot, string Source, string Destination, string StageDirectory);

public sealed class DataMigration(PlayPaths paths, IPlayActivity activity, IAvailableSpace? capacity = null)
{
    private static readonly string[] DataDirectories = ["runtimes", "state", "cache", "staging", "backups", "logs"];
    private static readonly string[] PrismDirectories = ["instances", "assets", "libraries", "meta", "icons"];
    private readonly IAvailableSpace _capacity = capacity ?? new AvailableSpace();
    private string PendingFile => PlayFiles.Child(paths.ControlRoot, "migration-pending.json");
    public async Task<MigrationPlan> PlanAsync(string destination, CancellationToken ct = default)
    {
        await activity.RequireIdleAsync("data-migration", ct);
        destination = ValidateDestination(destination);
        await EnsureCurrentAsync(ct);
        var files = Inventory();
        var bytes = checked(files.Sum(file => file.Length));
        var required = checked(bytes + 64L * 1024 * 1024);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any() && ReadPending(destination)?.PlanId != Fingerprint(files, destination))
            throw new DistributionException("migration_changed", "前回の一時コピーと元のデータが一致しません。別の空のフォルダーを選択してください。");
        if (_capacity.Bytes(destination) < required) throw new DistributionException("disk_space", "移行先の空き容量が不足しています。");
        return new(Fingerprint(files, destination), paths.Root, destination, bytes, required, files.Count);
    }
    public async Task<MigrationReceipt> MigrateAsync(MigrationPlan expected, IProgress<MigrationProgress>? progress = null, CancellationToken ct = default)
    {
        await using var gate = Lock(paths.ControlRoot, "migration.lock");
        await using var setup = Lock(paths.ControlRoot, "setup.lock");
        var plan = await PlanAsync(expected.Destination, ct);
        if (plan != expected) throw new DistributionException("migration_changed", "移行元または保存先が変わりました。もう一度確認してください。");
        var files = Inventory();
        var previous = ReadPending(plan.Destination);
        var id = previous?.PlanId == plan.Id ? previous.Id : Guid.NewGuid().ToString("N");
        var stageRoot = PlayFiles.Child(Path.GetDirectoryName(plan.Destination)!, ".mcmere-play-migration-" + id);
        var stage = new MigrationStage(id, plan.Id, paths.ControlRoot, paths.Root, plan.Destination, stageRoot);
        if (previous == stage && Directory.Exists(plan.Destination) && Directory.EnumerateFileSystemEntries(plan.Destination).Any())
        {
            CheckStage(plan.Destination, stage, files);
            if (Directory.Exists(stageRoot)) throw new DistributionException("migration_state", "一時コピーが複数あります。別の空の保存先を選択してください。");
            Directory.Move(plan.Destination, stageRoot);
        }
        if (Directory.Exists(stageRoot)) CheckStage(stageRoot, stage, files);
        var stageInfo = PlayFiles.Child(stageRoot, "migration-stage.json");
        await PlayFiles.WriteAtomicAsync(stageInfo, DistributionJson.Bytes(stage), ct);
        await PlayFiles.WriteAtomicAsync(PendingFile, DistributionJson.Bytes(stage), ct);
        var copied = 0L; var count = 0;
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var directory in Folders()) Directory.CreateDirectory(PlayFiles.Child(stageRoot, directory));
            foreach (var entry in files)
            {
                ct.ThrowIfCancellationRequested(); await activity.RequireIdleAsync("data-migration", ct);
                var source = PlayFiles.Child(paths.Root, entry.Path);
                var target = PlayFiles.Child(stageRoot, entry.Path);
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
                    {
                        if (input.Length != entry.Length) throw new DistributionException("migration_changed", "コピー中に元のファイルが変わりました。");
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
                        var buffer = new byte[131072]; int read; long total = 0;
                        while ((read = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            total += read;
                            if (total > entry.Length) throw new DistributionException("migration_changed", "コピー中に元のファイルが変わりました。");
                            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), ct);
                            progress?.Report(new("copying", copied + total, plan.Bytes, count, plan.Files));
                        }
                        await output.FlushAsync(ct);
                        output.Flush(true);
                        if (total != entry.Length) throw new DistributionException("migration_changed", "コピー中に元のファイルが変わりました。");
                        await output.DisposeAsync();
                        hashes[entry.Path] = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                    }
                    File.Move(temporary, target, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                File.SetLastWriteTimeUtc(target, new DateTime(entry.ModifiedTicks, DateTimeKind.Utc));
                copied += entry.Length; count++;
                progress?.Report(new("verifying", copied, plan.Bytes, count, plan.Files));
            }
            foreach (var entry in files)
            {
                await activity.RequireIdleAsync("data-migration", ct);
                var target = PlayFiles.Child(stageRoot, entry.Path);
                if (new FileInfo(target).Length != entry.Length || await PlayFiles.Sha512Async(target, ct) != hashes[entry.Path])
                    throw new DistributionException("migration_corrupt", "コピーしたデータの検証に失敗しました。");
            }
            if (Fingerprint(Inventory(), plan.Destination) != plan.Id) throw new DistributionException("migration_changed", "コピー中に元のデータが変更されました。切り替えずに終了します。");
            await RelocateProfilesAsync(stageRoot, plan.Destination, ct);
            var receipt = new MigrationReceipt(id, paths.ControlRoot, paths.Root, plan.Destination, plan.Files, plan.Bytes, DateTimeOffset.UtcNow);
            await PlayFiles.WriteAtomicAsync(DataLocation.Receipt(stageRoot), DistributionJson.Bytes(receipt), ct);
            await activity.RequireIdleAsync("data-migration", ct); await EnsureCurrentAsync(ct); ct.ThrowIfCancellationRequested();
            ValidateDestination(plan.Destination);
            progress?.Report(new("switching", plan.Bytes, plan.Bytes, count, plan.Files));
            await activity.RequireIdleAsync("data-migration", ct);
            if (Directory.Exists(plan.Destination)) Directory.Delete(plan.Destination, false);
            Directory.Move(stageRoot, plan.Destination);
            try
            {
                await PlayFiles.WriteAtomicAsync(DataLocation.Pointer(paths.ControlRoot), DistributionJson.Bytes(new DataLocationRecord(id, paths.ControlRoot, plan.Destination)), CancellationToken.None);
            }
            catch
            {
                if (Directory.Exists(plan.Destination) && !Directory.Exists(stageRoot)) Directory.Move(plan.Destination, stageRoot);
                throw;
            }
            try { File.Delete(PlayFiles.Child(plan.Destination, "migration-stage.json")); File.Delete(PendingFile); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            try { progress?.Report(new("complete", plan.Bytes, plan.Bytes, count, plan.Files)); }
            catch (Exception) { }
            return receipt;
        }
        catch
        {
            try
            {
                if (Directory.Exists(stageRoot))
                {
                    CheckStage(stageRoot, stage, files);
                    Directory.Delete(stageRoot, true);
                    File.Delete(PendingFile);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DistributionException) { }
            throw;
        }
    }
    private string ValidateDestination(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw new DistributionException("migration_destination", "移行先には絶対パスを指定してください。");
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        PlayFiles.NoLinksToRoot(destination);
        if (DataLocation.Same(destination, Path.GetPathRoot(destination)!) || destination.StartsWith("\\\\", StringComparison.Ordinal) ||
            DataLocation.Same(destination, paths.Root) || DataLocation.Within(destination, paths.Root) || DataLocation.Within(paths.Root, destination) ||
            DataLocation.Same(destination, paths.ControlRoot) || DataLocation.Within(destination, paths.ControlRoot) || DataLocation.Within(paths.ControlRoot, destination))
            throw new DistributionException("migration_destination", "現在の保存先と重ならない、ローカルの専用フォルダーを指定してください。");
        if (File.Exists(destination) || Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any() && ReadPending(destination) is null)
            throw new DistributionException("migration_destination", "移行先は空のフォルダーにしてください。");
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DistributionException("migration_destination", "移行先の親フォルダーが見つかりません。");
        return destination;
    }
    private async Task EnsureCurrentAsync(CancellationToken ct)
    {
        if (!DataLocation.Same(await DataLocation.ResolveAsync(paths.ControlRoot, ct), paths.Root))
            throw new DistributionException("migration_changed", "保存先がすでに変更されています。アプリを再起動してください。");
        if (File.Exists(PlayFiles.Child(paths.ControlRoot, ".setup-journal.json"))) throw new DistributionException("setup_recovery", "先にアプリ更新の復旧を完了してください。");
        foreach (var directory in Directory.EnumerateDirectories(paths.State))
            if (File.Exists(PlayFiles.Child(directory, "journal.json"))) throw new DistributionException("recovery_required", "移行前にMOD更新の復旧を完了してください。");
    }
    private List<MigrationFile> Inventory()
    {
        var files = new List<string>();
        foreach (var relative in Folders()) files.AddRange(Directory.EnumerateFiles(PlayFiles.Child(paths.Root, relative)).Where(file => !ExcludedRuntimeProfile(file)));
        files.AddRange(Directory.EnumerateFiles(paths.Root, "settings*.json", SearchOption.TopDirectoryOnly));
        if (files.Count > 1000000) throw new DistributionException("migration_size", "移行するファイルの数が上限を超えています。");
        return files.Select(file =>
        {
            PlayFiles.NoLinksToRoot(file);
            var relative = Path.GetRelativePath(paths.Root, file).Replace('\\', '/');
            ManifestValidation.RelativePath(relative);
            var info = new FileInfo(file);
            return new MigrationFile(relative, info.Length, info.LastWriteTimeUtc.Ticks);
        }).OrderBy(file => file.Path, StringComparer.Ordinal).ToList();
    }
    private IEnumerable<string> Folders()
    {
        IEnumerable<string> Walk(string root)
        {
            if (ExcludedRuntimeProfile(root)) yield break;
            PlayFiles.NoLinksToRoot(root);
            if (!Directory.Exists(root)) yield break;
            yield return Path.GetRelativePath(paths.Root, root).Replace('\\', '/');
            foreach (var child in Directory.EnumerateDirectories(root)) foreach (var item in Walk(child)) yield return item;
        }
        return DataDirectories.Select(name => PlayFiles.Child(paths.Root, name)).Concat(PrismDirectories.Select(name => PlayFiles.Child(paths.PrismData, name)))
            .SelectMany(Walk).Order(StringComparer.Ordinal);
    }
    private bool ExcludedRuntimeProfile(string file)
    {
        var prism = PlayFiles.Child(paths.Runtimes, "prism");
        if (!DataLocation.Within(file, prism)) return false;
        return Path.GetRelativePath(prism, file).Replace('\\', '/').Split('/').Skip(1)
            .Any(part => part.StartsWith("accounts", StringComparison.OrdinalIgnoreCase) || part.Equals("prismlauncher.cfg", StringComparison.OrdinalIgnoreCase));
    }
    private string Fingerprint(IReadOnlyList<MigrationFile> files, string destination) => Convert.ToHexString(SHA256.HashData(DistributionJson.Bytes(new { paths.Root, paths.ControlRoot, destination, files, folders = Folders().ToArray() }))).ToLowerInvariant();
    private MigrationStage? ReadPending(string destination)
    {
        if (!File.Exists(PendingFile)) return null;
        try
        {
            if (new FileInfo(PendingFile).Length > 16384) throw new JsonException();
            var stage = DistributionJson.Read<MigrationStage>(File.ReadAllBytes(PendingFile));
            if (!Guid.TryParseExact(stage.Id, "N", out _) || !DataLocation.Same(stage.ControlRoot, paths.ControlRoot) ||
                !DataLocation.Same(stage.Source, paths.Root) || !DataLocation.Same(stage.Destination, destination)) return null;
            if (!DataLocation.Same(stage.StageDirectory, PlayFiles.Child(Path.GetDirectoryName(destination)!, ".mcmere-play-migration-" + stage.Id))) throw new JsonException();
            return stage;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new DistributionException("migration_state", "前回の移行記録を確認できません。"); }
    }
    private static void CheckStage(string root, MigrationStage stage, IReadOnlyList<MigrationFile> files)
    {
        var marker = PlayFiles.Child(root, "migration-stage.json");
        if (!File.Exists(marker) || new FileInfo(marker).Length > 16384 || DistributionJson.Read<MigrationStage>(File.ReadAllBytes(marker)) != stage)
            throw new DistributionException("migration_state", "一時コピーの所有情報を確認できません。");
        var expected = files.Select(file => file.Path).Append("migration-stage.json").Append("migration-receipt.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in PlayFiles.Files(root).ToArray())
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (expected.Contains(relative)) continue;
            if (relative.Length > 37 && relative.EndsWith(".tmp", StringComparison.Ordinal) && relative[^37] == '.' &&
                Guid.TryParseExact(relative.Substring(relative.Length - 36, 32), "N", out _) && expected.Contains(relative[..^37]))
            { File.Delete(file); continue; }
            throw new DistributionException("migration_state", "一時コピーに別のファイルが含まれています。別の空の保存先を選択してください。");
        }
    }
    private async Task RelocateProfilesAsync(string stageRoot, string destination, CancellationToken ct)
    {
        var instances = PlayFiles.Child(stageRoot, "prism-data/instances");
        if (!Directory.Exists(instances)) return;
        foreach (var directory in Directory.EnumerateDirectories(instances))
        {
            var configuration = PlayFiles.Child(directory, "instance.cfg");
            if (!File.Exists(configuration)) continue;
            if (new FileInfo(configuration).Length > 65536) throw new DistributionException("invalid_profile", "Prismの設定が大きすぎます。");
            var before = await File.ReadAllTextAsync(configuration, ct);
            var after = PrismProfile.Relocate(before, paths.Root, destination);
            if (after != before) await PlayFiles.WriteAtomicAsync(configuration, Encoding.UTF8.GetBytes(after), ct);
        }
    }
    private static FileStream Lock(string root, string name)
    {
        try { return new FileStream(PlayFiles.Child(root, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DistributionException("migration_busy", "アプリ更新または別の移行処理が実行中です。"); }
    }
}
