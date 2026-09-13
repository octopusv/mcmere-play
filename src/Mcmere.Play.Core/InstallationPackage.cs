using System.Security.Cryptography;
using System.Text.Json;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record PackageFile(string Path, long Length, string Sha256);
public sealed record ApplicationPackage(string Product, string Version, IReadOnlyList<PackageFile> Files, int SchemaVersion = 1);
public sealed record InstallationInfo(string Product, string Version, string DataRoot, string AppDirectory, DateTimeOffset InstalledAt);
internal sealed record InstallationJournal(string Id, bool HadPrevious, string Phase, InstallationInfo Installation);
public interface IInstallationRegistration
{
    Task ApplyAsync(InstallationInfo installation, CancellationToken ct);
    Task RemoveAsync(InstallationInfo installation, CancellationToken ct);
}

public static class InstallationPackage
{
    public static readonly string[] RequiredFiles = ["mcmere-play.exe", "mcmere-play.dll", "ui/index.html", "LICENSE", "THIRD_PARTY_NOTICES.md", "Uninstall.ps1"];
    public static async Task<ApplicationPackage> VerifyAsync(string directory, CancellationToken ct = default)
    {
        var manifest = PlayFiles.Child(directory, "payload.json");
        if (!File.Exists(manifest) || new FileInfo(manifest).Length > 8 * 1024 * 1024)
            throw new DistributionException("invalid_payload", "セットアップの検証情報がありません。");
        ApplicationPackage package;
        try { package = DistributionJson.Read<ApplicationPackage>(await File.ReadAllBytesAsync(manifest, ct)); }
        catch (JsonException) { throw new DistributionException("invalid_payload", "セットアップの検証情報を読み取れません。"); }
        if (package.Product != "mcmere-play" || package.SchemaVersion != 1 || !Version.TryParse(package.Version, out _) ||
            package.Files is null || package.Files.Count is < 1 or > 20000)
            throw new DistributionException("invalid_payload", "セットアップの製品情報が一致しません。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in package.Files)
        {
            var path = PlayFiles.Child(directory, file.Path);
            ManifestValidation.Hash(file.Sha256, 64);
            if (!names.Add(file.Path) || file.Path is "payload.json" or "installation.json" || file.Length < 0 ||
                !File.Exists(path) || new FileInfo(path).Length != file.Length)
                throw new DistributionException("invalid_payload", "セットアップのファイル構成が一致しません。");
            await using var input = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).ToLowerInvariant();
            if (hash != file.Sha256) throw new DistributionException("payload_corrupt", "セットアップのファイルが破損しています: " + file.Path);
        }
        var actual = PlayFiles.Files(directory).Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Where(file => file is not ("payload.json" or "installation.json")).ToArray();
        if (actual.Length != names.Count || RequiredFiles.Any(file => !names.Contains(file)))
            throw new DistributionException("invalid_payload", "セットアップに必要なファイルがありません。");
        return package;
    }
}

public sealed class InstallationEngine(IInstallationRegistration registration, IAvailableSpace? capacity = null)
{
    public async Task<InstallationInfo> InstallAsync(string zipPath, string dataRoot, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (Path.TrimEndingDirectorySeparator(Path.GetPathRoot(dataRoot)!) == dataRoot) throw new DistributionException("invalid_destination", "ドライブの直下は保存先に指定できません。");
        PlayFiles.NoLinksToRoot(dataRoot); Directory.CreateDirectory(dataRoot);
        await using var setupLock = Lock(dataRoot, "setup.lock");
        await using var appLock = Lock(dataRoot, "application.lock");
        await RecoverAsync(dataRoot, ct);
        var app = PlayFiles.Child(dataRoot, "app");
        InstallationInfo? previous = null;
        if (Directory.Exists(app))
        {
            previous = await ReadInstallationAsync(app, ct);
            if (previous?.Product != "mcmere-play" || !previous.DataRoot.Equals(dataRoot, StringComparison.OrdinalIgnoreCase))
                throw new DistributionException("unowned_destination", "保存先に別のアプリがあります。専用フォルダーを選択してください。");
        }
        var id = Guid.NewGuid().ToString("N");
        var staging = PlayFiles.Child(dataRoot, ".setup/" + id);
        var backup = PlayFiles.Child(dataRoot, ".setup/previous-" + id);
        using (var archive = System.IO.Compression.ZipFile.OpenRead(zipPath))
        {
            var required = checked(archive.Entries.Sum(entry => entry.Length) + 32L * 1024 * 1024);
            if ((capacity ?? new AvailableSpace()).Bytes(dataRoot) < required) throw new DistributionException("disk_space", "インストールに必要な空き容量がありません。");
        }
        progress?.Report("セットアップの内容を検証しています");
        ApplicationPackage package;
        try
        {
            await SafeArchive.ExtractAsync(zipPath, staging, ct);
            if (File.Exists(PlayFiles.Child(staging, "installation.json"))) throw new DistributionException("invalid_payload", "配布物にインストール情報が含まれています。");
            package = await InstallationPackage.VerifyAsync(staging, ct);
        }
        catch
        {
            if (Directory.Exists(staging)) { _ = PlayFiles.Files(staging).ToArray(); Directory.Delete(staging, true); }
            throw;
        }
        var info = new InstallationInfo("mcmere-play", package.Version, dataRoot, app, DateTimeOffset.UtcNow);
        await PlayFiles.WriteAtomicAsync(PlayFiles.Child(staging, "installation.json"), DistributionJson.Bytes(info), ct);
        var journalPath = PlayFiles.Child(dataRoot, ".setup-journal.json");
        var journal = new InstallationJournal(id, previous is not null, "prepared", info);
        await PlayFiles.WriteAtomicAsync(journalPath, DistributionJson.Bytes(journal), ct);
        var committed = false;
        try
        {
            progress?.Report("アプリを配置しています");
            if (previous is not null) Directory.Move(app, backup);
            Directory.Move(staging, app);
            await PlayFiles.WriteAtomicAsync(journalPath, DistributionJson.Bytes(journal with { Phase = "switched" }), ct);
            progress?.Report("このPCに登録しています");
            await registration.ApplyAsync(info, ct);
            await PlayFiles.WriteAtomicAsync(journalPath, DistributionJson.Bytes(journal with { Phase = "committed" }), ct);
            committed = true;
            File.Delete(journalPath);
            progress?.Report("インストールが完了しました");
            return info;
        }
        catch
        {
            if (committed) throw;
            await RecoverAsync(dataRoot, CancellationToken.None);
            throw;
        }
    }
    public static async Task<InstallationInfo?> ReadInstallationAsync(string appDirectory, CancellationToken ct = default)
    {
        var file = PlayFiles.Child(appDirectory, "installation.json");
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 16384) throw new DistributionException("invalid_installation", "インストール情報が不正です。");
        try
        {
            var info = DistributionJson.Read<InstallationInfo>(await File.ReadAllBytesAsync(file, ct));
            if (!ValidInstallation(info, appDirectory))
                throw new DistributionException("invalid_installation", "インストール情報が一致しません。");
            return info;
        }
        catch (JsonException) { throw new DistributionException("invalid_installation", "インストール情報を読み取れません。"); }
    }
    private static bool ValidInstallation(InstallationInfo? info, string appDirectory) =>
        info is not null && info.Product == "mcmere-play" && Version.TryParse(info.Version, out _) &&
        Path.IsPathFullyQualified(info.DataRoot) && Path.IsPathFullyQualified(info.AppDirectory) &&
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(info.AppDirectory)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory)), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(Path.Combine(info.DataRoot, "app")).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory)), StringComparison.OrdinalIgnoreCase);

    private async Task RecoverAsync(string root, CancellationToken ct)
    {
        var path = PlayFiles.Child(root, ".setup-journal.json");
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 16384) throw new DistributionException("setup_recovery", "セットアップの回復情報が不正です。");
        InstallationJournal journal;
        try { journal = DistributionJson.Read<InstallationJournal>(await File.ReadAllBytesAsync(path, ct)); }
        catch (JsonException) { throw new DistributionException("setup_recovery", "セットアップの回復情報を読み取れません。"); }
        var app = PlayFiles.Child(root, "app");
        if (!Guid.TryParseExact(journal.Id, "N", out _) || journal.Phase is not ("prepared" or "switched" or "committed") || !ValidInstallation(journal.Installation, app))
            throw new DistributionException("setup_recovery", "セットアップの回復情報が一致しません。");
        var backup = PlayFiles.Child(root, ".setup/previous-" + journal.Id);
        var staging = PlayFiles.Child(root, ".setup/" + journal.Id);
        var failed = PlayFiles.Child(root, ".setup/failed-" + journal.Id);
        if (journal.Phase != "committed")
        {
            var newPlaced = Directory.Exists(app) && !Directory.Exists(failed) &&
                (journal.HadPrevious ? Directory.Exists(backup) : !Directory.Exists(staging));
            if (newPlaced)
            {
                var placed = await ReadInstallationAsync(app, ct);
                if (placed != journal.Installation) throw new DistributionException("setup_recovery", "回復対象のアプリを確認できません。");
                Directory.Move(app, failed);
            }
            if (journal.HadPrevious && Directory.Exists(backup) && !Directory.Exists(app)) Directory.Move(backup, app);
            if (journal.HadPrevious)
            {
                var previous = await ReadInstallationAsync(app, ct) ?? throw new DistributionException("setup_recovery", "以前のアプリを復元できません。");
                await registration.ApplyAsync(previous, ct);
            }
            else await registration.RemoveAsync(journal.Installation, ct);
        }
        else
        {
            var current = await ReadInstallationAsync(app, ct);
            if (current != journal.Installation) throw new DistributionException("setup_recovery", "確定済みのアプリを確認できません。");
            await registration.ApplyAsync(current, ct);
        }
        File.Delete(path);
    }
    private static FileStream Lock(string root, string name)
    {
        try { return new FileStream(PlayFiles.Child(root, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DistributionException("application_running", "mcmere Playと他のセットアップを終了してから再試行してください。"); }
    }
}
