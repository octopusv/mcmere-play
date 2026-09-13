using System.Diagnostics;
using System.Text;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public static class PrismProfile
{
    public const string SupportedVersion = "11.1.0";
    public static IReadOnlyDictionary<string, byte[]> Prepare(PackManifest manifest, string javaPath, int maximumMemoryMiB,
        string? previousConfiguration = null)
    {
        ManifestValidation.Validate(manifest);
        if (manifest.PrismVersion != SupportedVersion) throw new DistributionException("unsupported_prism", "このPrism版にはアプリの更新が必要です。");
        if (!Path.IsPathFullyQualified(javaPath) || javaPath.Any(c => c is '\n' or '\r' or '\0') ||
            !Path.GetFileName(javaPath).Equals("java.exe", StringComparison.OrdinalIgnoreCase))
            throw new DistributionException("invalid_java", "ゲーム用java.exeの絶対パスを指定してください。");
        if (maximumMemoryMiB is < 1024 or > 32768 || maximumMemoryMiB % 64 != 0)
            throw new DistributionException("invalid_memory", "メモリを64MiB単位で指定してください。");
        var cfg = new Dictionary<string, string>(StringComparer.Ordinal);
        if (previousConfiguration is not null)
        {
            if (previousConfiguration.Length > 65536) throw new DistributionException("invalid_profile", "Prismの設定が大きすぎます。");
            foreach (var line in previousConfiguration.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';') || trimmed == "[General]") continue;
                var split = trimmed.IndexOf('=');
                if (split < 1 || trimmed.StartsWith('[')) throw new DistributionException("invalid_profile", "Prismの設定形式を確認できません。");
                cfg[trimmed[..split]] = trimmed[(split + 1)..];
            }
        }
        cfg["InstanceType"] = "OneSix";
        cfg["name"] = CleanValue(manifest.ServerName);
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
        var configuration = "[General]\n" + string.Join('\n', cfg.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)) + "\n";
        var profile = new { formatVersion = 1, components = new[]
        {
            new { uid = "net.minecraft", version = manifest.MinecraftVersion, important = true },
            new { uid = "net.neoforged", version = manifest.Loader.Version, important = true }
        } };
        return new Dictionary<string, byte[]> { ["instance.cfg"] = Encoding.UTF8.GetBytes(configuration), ["mmc-pack.json"] = DistributionJson.Bytes(profile) };
    }

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
