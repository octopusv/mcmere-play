using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed class ClientFileProvider(DistributionClient client, VerifiedDownloads downloads, PlayPaths paths,
    IReadOnlyList<string> selectedFolders, IProgress<TransferProgress>? progress = null) : IFileProvider
{
    public async Task<string> GetAsync(PackFile file, CancellationToken ct)
    {
        var cache = PlayFiles.Child(paths.Cache, "sha512-" + file.Sha512);
        using (await CacheAccess.AcquireAsync(cache, ct))
        {
            if (File.Exists(cache) && new FileInfo(cache).Length == file.Length && await PlayFiles.Sha512Async(cache, ct) == file.Sha512) return cache;
            foreach (var folder in selectedFolders)
            {
                var count = 0;
                foreach (var candidate in PlayFiles.Files(folder))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++count > 20000) throw new DistributionException("reuse_folder_large", "選択したフォルダーが大きすぎます。MODフォルダーを選択してください。");
                    if (new FileInfo(candidate).Length != file.Length || !Path.GetExtension(candidate).Equals(Path.GetExtension(file.Path), StringComparison.OrdinalIgnoreCase)) continue;
                    if (await PlayFiles.Sha512Async(candidate, ct) != file.Sha512) continue;
                    var temp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await using (var input = File.OpenRead(candidate))
                        await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await input.CopyToAsync(output, ct);
                        if (await PlayFiles.Sha512Async(temp, ct) != file.Sha512) throw new DistributionException("reuse_changed", "再利用するファイルが変更されました。");
                        File.Move(temp, cache, true);
                        progress?.Report(new(file.Id, file.Length, file.Length));
                        return cache;
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
            }
        }
        return await client.DownloadAsync(file, downloads, paths.Cache, progress, ct);
    }
}
