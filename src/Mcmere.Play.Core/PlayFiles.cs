using System.Security.Cryptography;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed class PlayPaths
{
    public string Root { get; }
    public string PrismData => PlayFiles.Child(Root, "prism-data");
    public string Cache => PlayFiles.Child(Root, "cache/sha512");
    public string Staging => PlayFiles.Child(Root, "staging");
    public string Backups => PlayFiles.Child(Root, "backups");
    public string State => PlayFiles.Child(Root, "state");
    public string Runtimes => PlayFiles.Child(Root, "runtimes");
    public PlayPaths(string root)
    {
        Root = Path.GetFullPath(root);
        PlayFiles.NoLinksToRoot(Root);
        Directory.CreateDirectory(Root);
        foreach (var directory in new[] { PrismData, Cache, Staging, Backups, State, Runtimes }) Directory.CreateDirectory(directory);
    }
    public string InstanceId(Uri origin, string serverId)
    {
        ManifestValidation.Id(serverId);
        var prefix = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(origin.GetLeftPart(UriPartial.Authority)))).ToLowerInvariant()[..12];
        return "server-" + prefix + "-" + serverId;
    }
    public string Instance(string instanceId) => PlayFiles.Child(PrismData, "instances/" + instanceId);
    public string Game(string instanceId) => PlayFiles.Child(Instance(instanceId), ".minecraft");
}

public static class PlayFiles
{
    public static string Child(string root, string relative)
    {
        ManifestValidation.RelativePath(relative);
        var absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(absoluteRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(absoluteRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new DistributionException("invalid_path", "保存先の範囲外にはアクセスできません。");
        NoLinksToRoot(target);
        return target;
    }
    public static void NoLinksToRoot(string target)
    {
        for (string? cursor = Path.GetFullPath(target); cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new DistributionException("unsafe_link", "シンボリックリンクやジャンクションは利用できません。");
        }
    }
    public static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct = default)
    {
        NoLinksToRoot(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, ct);
                file.Flush(true);
            }
            NoLinksToRoot(path);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static async Task<string> Sha512Async(string path, CancellationToken ct = default)
    {
        NoLinksToRoot(path);
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA512.HashDataAsync(file, ct)).ToLowerInvariant();
    }
    public static IEnumerable<string> Files(string root)
    {
        NoLinksToRoot(root);
        if (!Directory.Exists(root)) yield break;
        foreach (var file in Directory.EnumerateFiles(root)) { NoLinksToRoot(file); yield return file; }
        foreach (var directory in Directory.EnumerateDirectories(root))
            foreach (var file in Files(directory)) yield return file;
    }
}
