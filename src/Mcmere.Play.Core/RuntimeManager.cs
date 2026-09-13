using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record RuntimeInstallation(string ArtifactId, string Version, string Directory, string Executable);
internal sealed record RuntimeFile(string Path, string Sha256, long Length);
internal sealed record RuntimeReceipt(string ArtifactId, string ArchiveSha256, IReadOnlyList<RuntimeFile> Files);
public sealed record JavaInspection(string Path, int Major, string Architecture, string VersionLine);

public sealed class RuntimeManager(PlayPaths paths, VerifiedDownloads downloads)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> Probes = new();
    public static bool IsProbe(int processId) => Probes.ContainsKey(processId);
    public async Task<RuntimeInstallation?> FindAsync(RuntimeArtifact artifact, string category, CancellationToken ct = default)
    {
        ManifestValidation.RelativePath(artifact.Id);
        if (artifact.Id.Contains('/') || category is not ("java" or "prism")) throw new DistributionException("invalid_runtime", "実行環境の識別情報が不正です。");
        var relative = category + "/" + artifact.Id;
        var pointer = PlayFiles.Child(paths.State, "runtime-" + artifact.Id + ".json");
        if (File.Exists(pointer))
        {
            if (new FileInfo(pointer).Length > 4096) throw new DistributionException("invalid_runtime", "実行環境の記録が不正です。");
            try { relative = DistributionJson.Read<string>(await File.ReadAllBytesAsync(pointer, ct)); }
            catch (System.Text.Json.JsonException) { return null; }
            if (!relative.StartsWith(category + "/" + artifact.Id, StringComparison.Ordinal) || relative.Count(c => c == '/') != 1)
                throw new DistributionException("invalid_runtime", "実行環境の保存先が不正です。");
        }
        var directory = PlayFiles.Child(paths.Runtimes, relative);
        return await ValidInstallationAsync(directory, artifact, ct) ? new(artifact.Id, artifact.Version, directory, PlayFiles.Child(directory, artifact.Executable)) : null;
    }
    public Task<RuntimeInstallation> EnsureJavaAsync(JavaRequirement requirement, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
        EnsureAsync(RuntimeCatalog.Java(requirement), "java", progress, ct);
    public Task<RuntimeInstallation> EnsurePrismAsync(IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
        EnsureAsync(RuntimeCatalog.Prism, "prism", progress, ct);

    public async Task<RuntimeInstallation> EnsureAsync(RuntimeArtifact artifact, string category,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        ManifestValidation.RelativePath(artifact.Id); ManifestValidation.RelativePath(artifact.Executable);
        ManifestValidation.Hash(artifact.Sha256, 64);
        if (artifact.Id.Contains('/') || category is not ("java" or "prism")) throw new DistributionException("invalid_runtime", "実行環境の識別情報が不正です。");
        await _gate.WaitAsync(ct);
        try
        {
            var basePath = PlayFiles.Child(paths.Runtimes, category + "/" + artifact.Id);
            var pointer = PlayFiles.Child(paths.State, "runtime-" + artifact.Id + ".json");
            string directory = basePath;
            if (File.Exists(pointer))
            {
                try
                {
                    if (new FileInfo(pointer).Length > 4096) throw new DistributionException("invalid_runtime", "実行環境の記録が不正です。");
                    var relative = DistributionJson.Read<string>(await File.ReadAllBytesAsync(pointer, ct));
                    if (!relative.StartsWith(category + "/" + artifact.Id, StringComparison.Ordinal) || relative.Count(c => c == '/') != 1)
                        throw new DistributionException("invalid_runtime", "実行環境の保存先が不正です。");
                    directory = PlayFiles.Child(paths.Runtimes, relative);
                }
                catch (System.Text.Json.JsonException) { directory = basePath; }
            }
            if (await ValidInstallationAsync(directory, artifact, ct))
                return new(artifact.Id, artifact.Version, directory, PlayFiles.Child(directory, artifact.Executable));
            var archive = await downloads.GetAsync(new(artifact.Id, artifact.Url, artifact.Length, artifact.Sha256, "sha256"), paths.Cache, progress: progress, ct: ct);
            var staging = PlayFiles.Child(paths.Staging, "runtime-" + Guid.NewGuid().ToString("N"));
            await SafeArchive.ExtractAsync(archive, staging, ct);
            if (!File.Exists(PlayFiles.Child(staging, artifact.Executable)))
                throw new DistributionException("runtime_executable_missing", "実行環境の配布物に必要なプログラムがありません。");
            var files = new List<RuntimeFile>();
            foreach (var file in PlayFiles.Files(staging))
                files.Add(new(Path.GetRelativePath(staging, file).Replace('\\', '/'), await Sha256Async(file, ct), new FileInfo(file).Length));
            await PlayFiles.WriteAtomicAsync(PlayFiles.Child(staging, "runtime-receipt.json"), DistributionJson.Bytes(new RuntimeReceipt(artifact.Id, artifact.Sha256, files)), ct);
            directory = Directory.Exists(basePath) ? basePath + "-repair-" + Guid.NewGuid().ToString("N") : basePath;
            PlayFiles.NoLinksToRoot(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            Directory.Move(staging, directory);
            await PlayFiles.WriteAtomicAsync(pointer, DistributionJson.Bytes(Path.GetRelativePath(paths.Runtimes, directory).Replace('\\', '/')), ct);
            return new(artifact.Id, artifact.Version, directory, PlayFiles.Child(directory, artifact.Executable));
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> ValidInstallationAsync(string directory, RuntimeArtifact artifact, CancellationToken ct)
    {
        var receiptPath = PlayFiles.Child(directory, "runtime-receipt.json");
        if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 8 * 1024 * 1024) return false;
        try
        {
            var receipt = DistributionJson.Read<RuntimeReceipt>(await File.ReadAllBytesAsync(receiptPath, ct));
            if (receipt.ArtifactId != artifact.Id || receipt.ArchiveSha256 != artifact.Sha256 || receipt.Files is null || receipt.Files.Count > 100000 ||
                !receipt.Files.Any(file => file.Path == artifact.Executable)) return false;
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in receipt.Files)
            {
                ManifestValidation.Hash(entry.Sha256, 64);
                var file = PlayFiles.Child(directory, entry.Path);
                if (!expected.Add(file) || !File.Exists(file) || new FileInfo(file).Length != entry.Length || await Sha256Async(file, ct) != entry.Sha256) return false;
            }
            return PlayFiles.Files(directory).All(file => expected.Contains(file) || file.Equals(receiptPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or DistributionException or NullReferenceException) { return false; }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        PlayFiles.NoLinksToRoot(path);
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
    }

    public static async Task<JavaInspection> InspectJavaAsync(string executable, int requiredMajor, CancellationToken ct = default)
    {
        PlayFiles.NoLinksToRoot(executable);
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable) || !Path.GetFileName(executable).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
            throw new DistributionException("invalid_java", "ゲーム用java.exeを確認できません。");
        var result = await ProbeAsync(executable, ["-XshowSettings:properties", "-version"], ct);
        var version = Regex.Match(result, "(?m)^(?:openjdk|java) version \"(?<version>(?<major>[0-9]+)[^\"]*)\"");
        var architecture = Regex.Match(result, "(?m)^\\s*os.arch\\s*=\\s*(?<arch>\\S+)\\s*$").Groups["arch"].Value;
        if (!version.Success || !int.TryParse(version.Groups["major"].Value, out var major) || major != requiredMajor || architecture is not ("amd64" or "x86_64"))
            throw new DistributionException("java_mismatch", "この構成には64bitのJava " + requiredMajor + "が必要です。");
        return new(executable, major, "x64", version.Value.Trim());
    }

    public static async Task<string> ProbeAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new DistributionException("probe_failed", "実行環境の確認を開始できません。");
        Probes[process.Id] = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var result = await stdout + "\n" + await stderr;
            if (process.ExitCode != 0) throw new DistributionException("probe_failed", "実行環境の確認に失敗しました。");
            return result;
        }
        catch
        {
            timeout.Cancel();
            // Only this short-lived version probe is owned by this method.
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr); } catch (Exception error) when (error is OperationCanceledException or IOException or DistributionException) { }
            throw;
        }
        finally { Probes.TryRemove(process.Id, out _); }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (text.Length + count > 131072) throw new DistributionException("probe_output", "実行環境の応答が大きすぎます。");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
