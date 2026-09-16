using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mcmere.Distribution.Contracts;

public sealed record PackArchiveInfo(bool Data, bool Resources, int Format, int MinimumFormat, int MaximumFormat,
    long Length, string Sha512, IReadOnlyList<string> Entries);

public static class PackArchive
{
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    public static async Task<PackArchiveInfo> InspectAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is < 1 or > MaximumBytes) Fail("ZIPのサイズが範囲外です。");
            var hash = Convert.ToHexString(await SHA512.HashDataAsync(input, ct)).ToLowerInvariant();
            input.Position = 0;
            using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
            if (zip.Entries.Count > 100000) Fail("ZIPの項目数が上限を超えています。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.TrimEnd('/');
                ManifestValidation.RelativePath(name);
                if (!names.Add(name) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                    (entry.ExternalAttributes & 0x400) != 0) Fail("ZIPに重複またはリンクが含まれています。");
                if (entry.FullName.EndsWith('/')) continue;
                total = checked(total + entry.Length);
                if (total > 8L * 1024 * 1024 * 1024 || entry.Length > MaximumBytes) Fail("展開サイズが上限を超えています。");
                files.Add(name);
                // Consume every stream with its declared limit; never extract into the game directory.
                await using var stream = entry.Open();
                var buffer = new byte[65536]; long read = 0; int count;
                while ((count = await stream.ReadAsync(buffer, ct)) != 0)
                    if ((read += count) > entry.Length) Fail("ZIPの展開サイズが一致しません。");
                if (read != entry.Length) Fail("ZIPのファイルが途中で切れています。");
            }
            var fileNames = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
                for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
                    if (fileNames.Contains(name[..slash])) Fail("ZIPのファイルとフォルダーが競合しています。");
            var metadata = zip.GetEntry("pack.mcmeta");
            if (metadata is null || metadata.Length > 1024 * 1024) Fail("ZIPのルートにpack.mcmetaが必要です。");
            await using var metaStream = metadata!.Open();
            using var doc = await JsonDocument.ParseAsync(metaStream, new() { MaxDepth = 32 }, ct);
            if (doc.RootElement.TryGetProperty("overlays", out _) || doc.RootElement.TryGetProperty("filter", out _))
                throw new DistributionException("pack_metadata_unsupported", "overlays/filterを含むパックは現在の自動管理に対応していません。");
            var pack = doc.RootElement.GetProperty("pack");
            var format = pack.GetProperty("pack_format").GetInt32();
            var minimum = format; var maximum = format;
            if (pack.TryGetProperty("supported_formats", out var range))
            {
                if (range.ValueKind == JsonValueKind.Number) minimum = maximum = range.GetInt32();
                else if (range.ValueKind == JsonValueKind.Array && range.GetArrayLength() == 2) { minimum = range[0].GetInt32(); maximum = range[1].GetInt32(); }
                else { minimum = range.GetProperty("min_inclusive").GetInt32(); maximum = range.GetProperty("max_inclusive").GetInt32(); }
            }
            if (minimum < 1 || maximum < minimum || format < minimum || format > maximum) Fail("パック形式の範囲が不正です。");
            var data = files.Any(name => name.StartsWith("data/", StringComparison.Ordinal));
            var resources = files.Any(name => name.StartsWith("assets/", StringComparison.Ordinal));
            if (!data && !resources) Fail("dataまたはassetsが必要です。");
            return new(data, resources, format, minimum, maximum, input.Length, hash, files);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { throw new DistributionException("pack_invalid_archive", "パックのZIPまたはmetadataを読み取れません。"); }
    }

    public static void RequireCompatible(PackArchiveInfo info, string minecraft, bool data, bool resources)
    {
        if (minecraft != "1.21.1" || data && (!info.Data || info.MinimumFormat > 48 || info.MaximumFormat < 48) ||
            resources && (!info.Resources || info.MinimumFormat > 34 || info.MaximumFormat < 34))
            throw new DistributionException("pack_format_incompatible", "このパックは指定されたMinecraft 1.21.1の用途に対応していません。");
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message) => throw new DistributionException("pack_invalid_archive", message);
}
