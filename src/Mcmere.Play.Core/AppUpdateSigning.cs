using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record AppUpdateManifest(int SchemaVersion, string Product, string Version, string Architecture, bool Prerelease,
    DateTimeOffset PublishedAt, string Notes, string SetupUrl, long Length, string Sha256);

public static partial class AppUpdateCatalog
{
    public const string Repository = "https://github.com/octopusv/mcmere-play";
    public const string ReleasesApi = "https://api.github.com/repos/octopusv/mcmere-play/releases?per_page=100";
    public static DistributionKey TrustedKey
    {
        get
        {
            using var stream = typeof(AppUpdateCatalog).Assembly.GetManifestResourceStream("app-update-key.json") ?? throw new InvalidOperationException("アプリ更新用の公開鍵がありません。");
            using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            return DistributionJson.Read<DistributionKey>(bytes.ToArray());
        }
    }
    public static bool IsVersion(string? text) => text is not null && VersionPattern().IsMatch(text) && Version.TryParse(text, out _);
    public static string Asset(string version, string name) => Repository + "/releases/download/v" + version + "/" + name;
    public static string SetupName(string version) => "mcmere-play-Setup-" + version + ".exe";
    [GeneratedRegex(@"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

public static class AppUpdateSigning
{
    public const int MaximumEnvelopeBytes = 65536;
    public static SignedManifest Sign(AppUpdateManifest manifest, RSA key)
    {
        Validate(manifest);
        if (key.KeySize < 2048) throw new DistributionException("update_key", "アプリ更新用の署名鍵が不正です。");
        var bytes = DistributionJson.Bytes(manifest);
        return new(ManifestSigning.PublicKey(key).KeyId, Convert.ToBase64String(bytes),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    }
    public static AppUpdateManifest Verify(SignedManifest envelope, DistributionKey keyInfo)
    {
        try
        {
            if (envelope.Payload is not { Length: > 0 and < 48000 } || envelope.Signature is not { Length: > 0 and < 4096 } ||
                keyInfo.PublicKey is not { Length: > 0 and < 16384 }) throw new FormatException();
            using var key = RSA.Create();
            var publicBytes = Convert.FromBase64String(keyInfo.PublicKey); key.ImportSubjectPublicKeyInfo(publicBytes, out var consumed);
            var bytes = Convert.FromBase64String(envelope.Payload);
            if (consumed != publicBytes.Length || key.KeySize < 2048 || ManifestSigning.PublicKey(key) != keyInfo || envelope.KeyId != keyInfo.KeyId ||
                envelope.Sha256 != Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() ||
                !key.VerifyData(bytes, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new FormatException();
            var manifest = DistributionJson.Read<AppUpdateManifest>(bytes); Validate(manifest); return manifest;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException or ArgumentException or NullReferenceException)
        { throw new DistributionException("update_signature", "アプリ更新情報の署名を確認できません。"); }
    }
    private static void Validate(AppUpdateManifest value)
    {
        if (value.SchemaVersion != 1 || value.Product != "mcmere-play" || !AppUpdateCatalog.IsVersion(value.Version) || value.Architecture != "win-x64" ||
            value.Notes is not { Length: <= 8000 } || value.Length is < 1 or > 1024L * 1024 * 1024 ||
            value.SetupUrl != AppUpdateCatalog.Asset(value.Version, AppUpdateCatalog.SetupName(value.Version)))
            throw new DistributionException("update_manifest", "対応しないアプリ更新情報です。");
        ManifestValidation.Hash(value.Sha256, 64);
    }
}
