using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record PackOptionValues(string? ResourcePacks, string? IncompatibleResourcePacks);
public sealed record PackOptionChange(PackOptionValues Before, PackOptionValues After);

public static class ResourcePackOptions
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int Limit = 2 * 1024 * 1024;
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length > Limit) throw new DistributionException("options_conflict", "options.txtのサイズが上限を超えています。");
        try { return Utf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw new DistributionException("options_conflict", "options.txtの文字コードを確認してください。"); }
    }
    public static PackOptionValues Read(byte[] bytes)
    {
        var text = Decode(bytes).TrimStart('\uFEFF');
        string? ReadKey(string key)
        {
            var matches = Regex.Matches(text, "(?m)^" + key + @":([^\r\n]*)");
            if (matches.Count > 1) throw new DistributionException("options_conflict", "パック選択の設定が重複しています。");
            var value = matches.Count == 0 ? null : matches[0].Groups[1].Value;
            _ = Parse(value); return value;
        }
        return new(ReadKey("resourcePacks"), ReadKey("incompatibleResourcePacks"));
    }
    private static string[] Parse(string? value)
    {
        if (value is null) return [];
        try
        {
            var list = JsonSerializer.Deserialize<string[]>(value);
            if (list is null || list.Length > 4096 || list.Any(item => item is null || item.Length > 512 || item.Any(char.IsControl))) throw new JsonException();
            return list;
        }
        catch (JsonException) { throw new DistributionException("options_conflict", "パック選択のJSONが不正です。個人設定は変更していません。"); }
    }
    public static void Validate(PackOptionChange change)
    {
        if (change?.Before is null || change.After is null) throw new DistributionException("invalid_journal", "パック選択の回復記録が不正です。");
        foreach (var value in new[] { change.Before.ResourcePacks, change.Before.IncompatibleResourcePacks, change.After.ResourcePacks, change.After.IncompatibleResourcePacks })
        { if (value?.Length > Limit) throw new DistributionException("invalid_journal", "パック選択の回復記録が大きすぎます。"); _ = Parse(value); }
    }
    public static PackOptionChange Plan(byte[] bytes, PackManifest manifest, PackManifest? previous, IReadOnlyList<PackFile> selected)
    {
        var before = Read(bytes);
        static IEnumerable<string> PackIds(PackManifest? pack) => pack?.ResourcePacks.Select(item => "file/" + pack.Files.Single(file => file.Id == item.FileId).Path["resourcepacks/".Length..]) ?? [];
        var owned = PackIds(manifest).Concat(PackIds(previous)).ToHashSet(StringComparer.Ordinal);
        var enabled = manifest.ResourcePacks.Where(item => selected.Any(file => file.Id == item.FileId))
            .Select(item => "file/" + manifest.Files.Single(file => file.Id == item.FileId).Path["resourcepacks/".Length..]).Reverse().ToArray();
        var ids = Parse(before.ResourcePacks).Where(id => !owned.Contains(id)).ToList();
        // NeoForge 21.1 adds a missing required mod_resources pack at Position.TOP,
        // after file packs. Persist it explicitly so its children stay below our overrides
        // even before the first Minecraft launch. options.txt is low-to-high priority.
        if (enabled.Length > 0 && manifest.Loader.Kind == "neoforge" && !ids.Contains("mod_resources"))
            ids.Insert(ids.IndexOf("vanilla") + 1, "mod_resources");
        ids.AddRange(enabled);
        var incompatible = Parse(before.IncompatibleResourcePacks).Where(id => !owned.Contains(id)).ToArray();
        var after = new PackOptionValues(
            ids.SequenceEqual(Parse(before.ResourcePacks)) ? before.ResourcePacks : JsonSerializer.Serialize(ids),
            incompatible.SequenceEqual(Parse(before.IncompatibleResourcePacks)) ? before.IncompatibleResourcePacks : JsonSerializer.Serialize(incompatible));
        return new(before, after);
    }
    public static byte[] Write(byte[] bytes, PackOptionValues values)
    {
        var text = Decode(bytes); var bom = text.StartsWith('\uFEFF');
        if (bom) text = text[1..];
        _ = Read(bytes);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        foreach (var (key, value) in new[] { ("resourcePacks", values.ResourcePacks), ("incompatibleResourcePacks", values.IncompatibleResourcePacks) })
        {
            _ = Parse(value);
            var pattern = "(?m)^" + key + @":[^\r\n]*(?:\r?\n|$)";
            var match = Regex.Match(text, pattern);
            if (match.Success)
            {
                var ending = match.Value.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : match.Value.EndsWith('\n') ? "\n" : "";
                text = text[..match.Index] + (value is null ? "" : key + ":" + value + ending) + text[(match.Index + match.Length)..];
            }
            else if (value is not null) text += (text.Length > 0 && !text.EndsWith('\n') ? newline : "") + key + ":" + value + newline;
        }
        return Utf8.GetBytes((bom ? "\uFEFF" : "") + text);
    }
    public static byte[] Restore(byte[] current, PackOptionChange change)
    {
        Validate(change);
        var actual = Read(current);
        if (actual == change.Before) return current;
        if (actual != change.After) throw new DistributionException("recovery_conflict", "更新後にパック選択が変更されています。復旧を保留しました。");
        return Write(current, change.Before);
    }
}
