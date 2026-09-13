using System.IO.Compression;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public static class SafeArchive
{
    public static async Task ExtractAsync(string archivePath, string destination, CancellationToken ct = default)
    {
        PlayFiles.NoLinksToRoot(archivePath); PlayFiles.NoLinksToRoot(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new DistributionException("extraction_exists", "展開先には空のフォルダーを指定してください。");
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 100000 || archive.Entries.Sum(entry => entry.Length) > 4L * 1024 * 1024 * 1024)
            throw new DistributionException("archive_size", "展開サイズが上限を超えています。");
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.TrimEnd('/');
            if (name.Length == 0) continue;
            ManifestValidation.RelativePath(name);
            var directory = entry.FullName.EndsWith('/');
            if (!paths.TryAdd(name, directory) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new DistributionException("unsafe_archive", "重複する保存先またはリンクが含まれています。");
        }
        foreach (var path in paths.Where(entry => !entry.Value).Select(entry => entry.Key))
            if (paths.Keys.Any(other => other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                throw new DistributionException("unsafe_archive", "ファイルとフォルダーの保存先が競合しています。");
        Directory.CreateDirectory(destination);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.TrimEnd('/');
            if (name.Length == 0) continue;
            var target = PlayFiles.Child(destination, name);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[131072];
            long copied = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                copied += count;
                if (copied > entry.Length) throw new DistributionException("archive_size", "展開したファイルのサイズが不正です。");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (copied != entry.Length) throw new DistributionException("archive_corrupt", "展開したファイルが破損しています。");
        }
    }
}
