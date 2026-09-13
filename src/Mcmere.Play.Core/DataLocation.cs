using System.Text.Json;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record DataLocationRecord(string Id, string ControlRoot, string DataRoot, int SchemaVersion = 1, string Product = "mcmere-play");
public sealed record MigrationReceipt(string Id, string ControlRoot, string SourceRoot, string DataRoot, int FileCount, long Bytes, DateTimeOffset CompletedAt, int SchemaVersion = 1);
public static class DataLocation
{
    public static string Pointer(string controlRoot) => PlayFiles.Child(controlRoot, "data-location.json");
    public static string Receipt(string root) => PlayFiles.Child(root, "migration-receipt.json");
    public static async Task<string> ResolveAsync(string controlRoot, CancellationToken ct = default)
    {
        controlRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(controlRoot));
        var path = Pointer(controlRoot);
        if (!File.Exists(path)) return controlRoot;
        try
        {
            if (new FileInfo(path).Length > 16384) throw new JsonException();
            var location = DistributionJson.Read<DataLocationRecord>(await File.ReadAllBytesAsync(path, ct));
            if (location.SchemaVersion != 1 || location.Product != "mcmere-play" || !Guid.TryParseExact(location.Id, "N", out _) ||
                !Same(location.ControlRoot, controlRoot) || !Path.IsPathFullyQualified(location.DataRoot)) throw new JsonException();
            PlayFiles.NoLinksToRoot(location.DataRoot);
            var receiptPath = Receipt(location.DataRoot);
            if (!Directory.Exists(location.DataRoot) || !File.Exists(receiptPath))
                throw new DistributionException("data_location_missing", "データ保存先が見つかりません。ドライブを接続してから起動してください: " + location.DataRoot);
            if (new FileInfo(receiptPath).Length > 16384) throw new JsonException();
            var receipt = DistributionJson.Read<MigrationReceipt>(await File.ReadAllBytesAsync(receiptPath, ct));
            if (receipt.SchemaVersion != 1 || receipt.Id != location.Id || !Same(receipt.ControlRoot, controlRoot) || !Same(receipt.DataRoot, location.DataRoot)) throw new JsonException();
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(location.DataRoot));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException)
        { throw new DistributionException("data_location_invalid", "データ保存先の記録を確認できません。元の記録を確認してください。"); }
    }
    public static bool Same(string left, string right) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(left))
        .Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    public static bool Within(string child, string parent) => Path.GetFullPath(child).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
