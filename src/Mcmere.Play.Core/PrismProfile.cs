using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public static class PrismProfile
{
    public const string SupportedVersion = "11.1.0";
    public static string Relocate(string configuration, string sourceRoot, string destinationRoot)
    {
        var lines = configuration.Split('\n');
        var result = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            var split = trimmed.IndexOf('=');
            if (split > 0 && trimmed[..split] == "InstanceAccountId") continue;
            if (split > 0 && trimmed[..split] == "JavaPath")
            {
                var raw = trimmed[(split + 1)..];
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') raw = raw[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
                var old = raw.Replace('\\', '/');
                var source = Path.GetFullPath(sourceRoot).Replace('\\', '/').TrimEnd('/') + "/";
                if (old.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                {
                    var relative = old[source.Length..];
                    var target = PlayFiles.Child(destinationRoot, relative).Replace('\\', '/');
                    result.Add("JavaPath=" + target + (line.EndsWith('\r') ? "\r" : "")); continue;
                }
            }
            result.Add(line);
        }
        return string.Join('\n', result);
    }
    public static IReadOnlyDictionary<string, byte[]> Prepare(PackManifest manifest, string javaPath, int maximumMemoryMiB,
        string? previousConfiguration = null, string? previousPack = null)
    {
        ManifestValidation.Validate(manifest);
        if (manifest.PrismVersion != SupportedVersion) throw new DistributionException("unsupported_prism", "このPrism版にはアプリの更新が必要です。");
        if (!Path.IsPathFullyQualified(javaPath) || javaPath.Any(c => c is '\n' or '\r' or '\0') ||
            !Path.GetFileName(javaPath).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
            throw new DistributionException("invalid_java", "ゲーム用java.exeの絶対パスを指定してください。");
        if (maximumMemoryMiB is < 1024 or > 32768 || maximumMemoryMiB % 64 != 0)
            throw new DistributionException("invalid_memory", "メモリを64MiB単位で指定してください。");
        var old = ParseConfiguration(previousConfiguration);
        var cfg = new Dictionary<string, string>(old, StringComparer.Ordinal);
        cfg["InstanceType"] = "OneSix";
        cfg["name"] = Quote(CleanValue(manifest.ServerName));
        cfg["iconKey"] = "default";
        cfg["OverrideJavaLocation"] = "true";
        cfg["JavaPath"] = javaPath.Replace('\\', '/');
        cfg["AutomaticJava"] = "false";
        cfg["IgnoreJavaCompatibility"] = "false";
        cfg["OverrideJavaArgs"] = "true";
        cfg["JvmArgs"] = "";
        cfg["OverrideCommands"] = "true";
        cfg["PreLaunchCommand"] = "";
        cfg["PostExitCommand"] = "";
        cfg["WrapperCommand"] = "";
        cfg["OverrideMemory"] = "true";
        cfg["MinMemAlloc"] = "1024";
        cfg["MaxMemAlloc"] = maximumMemoryMiB.ToString(System.Globalization.CultureInfo.InvariantCulture);
        cfg["UseLatestMinecraftVersion"] = "false";
        cfg["JoinServerOnLaunch"] = "true";
        cfg["JoinServerOnLaunchAddress"] = manifest.GameEndpoint;
        var unchanged = previousConfiguration is not null && cfg.Count == old.Count && cfg.All(pair => old.TryGetValue(pair.Key, out var value) && EqualValue(pair.Key, value, pair.Value));
        var configuration = unchanged ? previousConfiguration! : "[General]\n" + string.Join('\n', cfg.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)) + "\n";
        var profile = new { formatVersion = 1, components = new[]
        {
            new { uid = "net.minecraft", version = manifest.MinecraftVersion, important = true },
            new { uid = "net.neoforged", version = manifest.Loader.Version, important = true }
        } };
        var pack = PackMatches(previousPack, manifest) ? Encoding.UTF8.GetBytes(previousPack!) : DistributionJson.Bytes(profile);
        return new Dictionary<string, byte[]> { ["instance.cfg"] = Encoding.UTF8.GetBytes(configuration), ["mmc-pack.json"] = pack };
    }

    public static bool PackMatches(string? json, PackManifest manifest)
    {
        if (json is null || json.Length > 1024 * 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("formatVersion").GetInt32() != 1) return false;
            var components = root.GetProperty("components").EnumerateArray().ToArray();
            if (components.Length is < 2 or > 20) return false;
            if (components.Select(item => item.GetProperty("uid").GetString()).Distinct(StringComparer.Ordinal).Count() != components.Length) return false;
            bool Expected(string id, string version) => components.Count(item => item.GetProperty("uid").GetString() == id &&
                item.GetProperty("version").GetString() == version && (!item.TryGetProperty("disabled", out var disabled) || !disabled.GetBoolean())) == 1;
            return Expected("net.minecraft", manifest.MinecraftVersion) && Expected("net.neoforged", manifest.Loader.Version) &&
                components.All(item => item.GetProperty("uid").GetString() is "net.minecraft" or "net.neoforged" ||
                    (item.GetProperty("uid").GetString() is "org.lwjgl" or "org.lwjgl3" && item.TryGetProperty("dependencyOnly", out var dependency) && dependency.GetBoolean()));
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }
    private static Dictionary<string, string> ParseConfiguration(string? value)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (value is null) return result;
        if (value.Length > 65536) throw new DistributionException("invalid_profile", "Prismの設定が大きすぎます。");
        foreach (var line in value.TrimStart('\uFEFF').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';') || trimmed == "[General]") continue;
            var split = trimmed.IndexOf('=');
            if (split < 1 || trimmed.StartsWith('[')) return new(StringComparer.Ordinal);
            result[trimmed[..split]] = trimmed[(split + 1)..];
        }
        return result;
    }
    private static bool EqualValue(string key, string left, string right)
    {
        static string Decode(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
            return value;
        }
        left = Decode(left); right = Decode(right);
        return key == "JavaPath" ? left.Replace('\\', '/').Equals(right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) : left == right;
    }
    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public static ProcessStartInfo Launch(string executable, PlayPaths paths, string instanceId, string endpoint)
    {
        ManifestValidation.RelativePath(instanceId);
        if (instanceId.Contains('/')) throw new DistributionException("invalid_instance", "インスタンスのIDが不正です。");
        ManifestValidation.Endpoint(endpoint);
        var command = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (var argument in new[] { "--dir", paths.PrismData, "--launch", instanceId, "--server", endpoint }) command.ArgumentList.Add(argument);
        return command;
    }
    private static string CleanValue(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
}
