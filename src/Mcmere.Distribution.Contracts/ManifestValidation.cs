using System.Text.RegularExpressions;

namespace Mcmere.Distribution.Contracts;

public static class ManifestValidation
{
    public static string Name(string name)
    {
        var value = name?.Trim() ?? "";
        if (!Regex.IsMatch(value, "^[A-Za-z0-9_]{3,16}$"))
            throw new DistributionException("name_invalid", "Minecraft名は英数字・アンダースコアで3〜16文字にしてください。");
        return value;
    }

    public static void Validate(PackManifest manifest)
    {
        if (manifest.SchemaVersion != 1) Fail("unsupported_schema", "この配布形式にはアプリの更新が必要です。");
        Id(manifest.ReleaseId); Id(manifest.ServerPublicId);
        if (manifest.Sequence < 1 || manifest.CreatedAt == default) Fail("invalid_manifest", "配布版の識別情報が不正です。");
        Text(manifest.ServerName, 100); Text(manifest.DisplayVersion, 100); Text(manifest.Changelog, 20000, empty: true);
        Version(manifest.MinecraftVersion); Version(manifest.MinimumPlayVersion); Version(manifest.PrismVersion);
        if (manifest.Loader is null || manifest.Loader.Kind != "neoforge") Fail("unsupported_loader", "対応するNeoForge構成を指定してください。");
        Version(manifest.Loader!.Version);
        if (manifest.Java is null || manifest.Java.Major is < 17 or > 99 || manifest.Java.Architecture != "x64")
            Fail("unsupported_java", "対応する64bit Java構成を指定してください。");
        Text(manifest.Java!.RuntimeCatalogId, 120);
        if (manifest.RecommendedMemoryMiB is < 1024 or > 32768 || manifest.RecommendedMemoryMiB % 64 != 0)
            Fail("invalid_memory", "推奨メモリの設定が不正です。");
        Endpoint(manifest.GameEndpoint);
        Hash(manifest.ServerFingerprint, 64); Hash(manifest.ClientFingerprint, 64);
        if (manifest.Files is null || manifest.Files.Count > 4096) Fail("invalid_files", "配布ファイルの数が不正です。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var modIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files!)
        {
            if (file is null) Fail("invalid_files", "配布ファイルの情報がありません。");
            Text(file!.Id, 100);
            if (!Regex.IsMatch(file.Id, "^[A-Za-z0-9_-]+$") || !ids.Add(file.Id)) Fail("duplicate_file", "配布ファイルIDが不正または重複しています。");
            RelativePath(file.Path);
            var prefix = file.Path.Split('/')[0];
            if (prefix is not ("mods" or "config" or "defaultconfigs" or "resourcepacks" or "shaderpacks"))
                Fail("protected_path", "配布対象外のフォルダーが指定されています。");
            if (!paths.Add(file.Path)) Fail("duplicate_path", "配布ファイルの保存先が重複しています。");
            if (file.Length is < 1 or > 2L * 1024 * 1024 * 1024) Fail("invalid_size", "配布ファイルのサイズが不正です。");
            total = checked(total + file.Length);
            if (total > 16L * 1024 * 1024 * 1024) Fail("invalid_size", "配布ファイルの合計サイズが上限を超えています。");
            Hash(file.Sha512, 128); Text(file.Name, 200); Text(file.Version, 100);
            if (!Enum.IsDefined(file.Requirement) || !Enum.IsDefined(file.UpdatePolicy) || file.Source is null || !Enum.IsDefined(file.Source.Kind))
                Fail("invalid_file", "配布ファイルの設定が不正です。");
            if (prefix == "mods" && (!file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || file.UpdatePolicy != FileUpdatePolicy.Managed))
                Fail("invalid_mod", "MODは管理対象のJARとして指定してください。");
            if (file.ModIds is null || file.Requires is null || file.ModIds.Count > 256 || file.Requires.Count > 512)
                Fail("invalid_dependencies", "依存MODの情報が不正です。");
            foreach (var modId in file.ModIds!)
                if (!Regex.IsMatch(modId ?? "", "^[A-Za-z0-9_.-]{1,100}$") || !modIds.Add(modId!))
                    Fail("duplicate_mod", "同じMODを提供するファイルが重複しています。");
            if (file.Source!.Kind is FileSourceKind.Modrinth or FileSourceKind.Direct) Https(file.Source.Url);
            if (file.Source.Kind == FileSourceKind.Manual) Https(file.Source.PageUrl);
            if (file.Source.Kind == FileSourceKind.Hosted) Hash(file.Source.BlobId, 128);
        }
        var byId = manifest.Files!.ToDictionary(file => file.Id, StringComparer.Ordinal);
        foreach (var file in manifest.Files!)
            foreach (var dependency in file.Requires)
                if (dependency is null || !byId.ContainsKey(dependency)) Fail("missing_dependency", "配布に必要な依存ファイルがありません。");
        foreach (var path in paths)
            if (paths.Any(other => other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                Fail("duplicate_path", "ファイルとフォルダーの保存先が競合しています。");
    }

    public static IReadOnlyList<PackFile> Selected(PackManifest manifest, ISet<string>? optionalIds = null)
    {
        Validate(manifest);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var byId = manifest.Files.ToDictionary(file => file.Id, StringComparer.Ordinal);
        void Add(PackFile file)
        {
            if (!selected.Add(file.Id)) return;
            foreach (var dependency in file.Requires) Add(byId[dependency]);
        }
        foreach (var file in manifest.Files)
            if (file.Requirement == FileRequirement.Required || (optionalIds?.Contains(file.Id) ?? file.DefaultEnabled)) Add(file);
        return manifest.Files.Where(file => selected.Contains(file.Id)).ToArray();
    }

    public static void RelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') ||
            path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c)) || Regex.IsMatch(part, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)", RegexOptions.IgnoreCase)))
            Fail("invalid_path", "保存先のパスが不正です。");
    }
    public static void Id(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) Fail("invalid_id", "サーバーまたは配布版のIDが不正です。");
    }
    public static void Hash(string? hash, int length)
    {
        if (hash is null || hash.Length != length || !Regex.IsMatch(hash, "^[0-9a-f]+$")) Fail("invalid_hash", "ファイルのハッシュが不正です。");
    }
    public static void Version(string version)
    {
        if (!Regex.IsMatch(version ?? "", "^[A-Za-z0-9][A-Za-z0-9._+-]{0,79}$")) Fail("invalid_version", "バージョン表記が不正です。");
    }
    public static void Endpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 260 || !Uri.TryCreate("minecraft://" + endpoint, UriKind.Absolute, out var uri) ||
            uri.HostNameType is UriHostNameType.Unknown || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath is not ("" or "/") || endpoint.Any(char.IsWhiteSpace) || endpoint.Contains('/') ||
            uri.Port is < -1 or 0 or > 65535)
            Fail("invalid_endpoint", "ゲームサーバーの接続先が不正です。");
    }
    public static Uri Https(string? address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) Fail("invalid_url", "配布元はHTTPSで指定してください。");
        return uri!;
    }
    private static void Text(string? value, int maximum, bool empty = false)
    {
        if (value is null || (!empty && string.IsNullOrWhiteSpace(value)) || value.Length > maximum || value.Contains('\0'))
            Fail("invalid_text", "配布情報の文字列が不正です。");
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string message) => throw new DistributionException(code, message);
}
