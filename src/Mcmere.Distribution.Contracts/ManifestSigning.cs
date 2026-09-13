using System.Security.Cryptography;

namespace Mcmere.Distribution.Contracts;

public static class ManifestSigning
{
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
    public static DistributionKey PublicKey(RSA key)
    {
        var bytes = key.ExportSubjectPublicKeyInfo();
        return new DistributionKey(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Convert.ToBase64String(bytes));
    }
    public static SignedManifest Sign(PackManifest manifest, RSA key)
    {
        ManifestValidation.Validate(manifest);
        if (key.KeySize < 2048) throw new DistributionException("invalid_key", "署名鍵の長さが不足しています。");
        var bytes = DistributionJson.Bytes(manifest);
        if (bytes.Length > MaximumPayloadBytes) throw new DistributionException("manifest_too_large", "配布情報が大きすぎます。");
        return new SignedManifest(PublicKey(key).KeyId, Convert.ToBase64String(bytes),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    }
    public static PackManifest Verify(SignedManifest envelope, DistributionKey trustedKey, string expectedServerId, long minimumSequence = 1)
    {
        try
        {
            if (envelope.Payload is null || envelope.Payload.Length > (MaximumPayloadBytes + 2) / 3 * 4 ||
                envelope.Signature is null || envelope.Signature.Length > 4096 || trustedKey.PublicKey.Length > 16384)
                throw new DistributionException("invalid_signature", "配布情報の署名を確認できません。");
            var bytes = Convert.FromBase64String(envelope.Payload);
            using var key = RSA.Create();
            var publicBytes = Convert.FromBase64String(trustedKey.PublicKey);
            key.ImportSubjectPublicKeyInfo(publicBytes, out var read);
            if (read != publicBytes.Length || key.KeySize < 2048 || trustedKey.KeyId != PublicKey(key).KeyId || envelope.KeyId != trustedKey.KeyId ||
                envelope.Sha256 != Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() ||
                !key.VerifyData(bytes, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new DistributionException("invalid_signature", "配布情報の署名を確認できません。");
            var manifest = DistributionJson.Read<PackManifest>(bytes);
            ManifestValidation.Validate(manifest);
            if (manifest.ServerPublicId != expectedServerId) throw new DistributionException("server_mismatch", "配布先のサーバーが一致しません。");
            if (manifest.Sequence < minimumSequence) throw new DistributionException("release_outdated", "古い配布版が指定されています。配布情報を確認し直してください。");
            return manifest;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or System.Text.Json.JsonException or ArgumentException or NullReferenceException)
        {
            throw new DistributionException("invalid_signature", "配布情報の署名または形式を確認できません。");
        }
    }
}
