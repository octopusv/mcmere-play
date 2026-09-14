using System.Security.Cryptography;
using System.Text.Json;

namespace Mcmere.Distribution.Contracts;

public sealed record KeyTransition(string Purpose, int SchemaVersion, string ServerPublicId, string PreviousKeyId, DistributionKey NextKey, long MinimumSequence);
public sealed record SignedKeyTransition(string KeyId, string Payload, string Sha256, string Signature);
public sealed record RotatedTrust(DistributionKey Key, long MinimumSequence);

public static class KeyRotation
{
    public const int MaximumTransitions = 16;
    public static SignedKeyTransition Sign(string publicId, DistributionKey next, long minimumSequence, RSA previous)
    {
        ManifestValidation.Id(publicId); ValidateKey(next);
        var old = ManifestSigning.PublicKey(previous);
        if (previous.KeySize < 2048 || next.KeyId == old.KeyId || minimumSequence < 1) throw Failure();
        var bytes = DistributionJson.Bytes(new KeyTransition("mcmere.pack-key-transition", 1, publicId, old.KeyId, next, minimumSequence));
        return new(old.KeyId, Convert.ToBase64String(bytes), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Convert.ToBase64String(previous.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    }
    public static RotatedTrust Verify(DistributionKey trusted, DistributionKey advertised, IReadOnlyList<SignedKeyTransition>? transitions,
        string publicId, long minimumSequence = 0)
    {
        ValidateKey(trusted); ValidateKey(advertised); ManifestValidation.Id(publicId);
        if (trusted == advertised) return new(trusted, minimumSequence);
        if (transitions is null || transitions.Count is < 1 or > MaximumTransitions) throw Failure();
        var start = -1;
        for (var i = 0; i < transitions.Count; i++) if (transitions[i]?.KeyId == trusted.KeyId) { start = i; break; }
        if (start < 0) throw Failure();
        var seen = new HashSet<string>(StringComparer.Ordinal) { trusted.KeyId };
        try
        {
            for (var i = start; i < transitions.Count; i++)
            {
                var envelope = transitions[i];
                if (envelope is null || envelope.KeyId != trusted.KeyId || envelope.Payload is not { Length: > 0 and < 12000 } ||
                    envelope.Signature is not { Length: > 0 and < 4096 }) throw Failure();
                var bytes = Convert.FromBase64String(envelope.Payload);
                using var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(trusted.PublicKey), out _);
                if (envelope.Sha256 != Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() ||
                    !key.VerifyData(bytes, Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw Failure();
                var change = DistributionJson.Read<KeyTransition>(bytes);
                ValidateKey(change.NextKey);
                if (change.Purpose != "mcmere.pack-key-transition" || change.SchemaVersion != 1 || change.ServerPublicId != publicId ||
                    change.PreviousKeyId != trusted.KeyId || change.MinimumSequence <= minimumSequence || !seen.Add(change.NextKey.KeyId)) throw Failure();
                trusted = change.NextKey; minimumSequence = change.MinimumSequence;
            }
            if (trusted != advertised) throw Failure();
            return new(trusted, minimumSequence);
        }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException or ArgumentException or NullReferenceException)
        { throw Failure(); }
    }
    public static void ValidateKey(DistributionKey key)
    {
        try
        {
            if (key.PublicKey is not { Length: > 0 and < 16384 }) throw new FormatException();
            var bytes = Convert.FromBase64String(key.PublicKey);
            using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            if (consumed != bytes.Length || rsa.KeySize < 2048 || ManifestSigning.PublicKey(rsa) != key) throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or CryptographicException or ArgumentException or NullReferenceException)
        { throw new DistributionException("invalid_key", "配布元の公開鍵を確認できません。"); }
    }
    private static DistributionException Failure() => new("signing_key_changed", "配布元の新しい公開鍵を検証できません。管理者に確認し、必要な場合は登録を外して配布ページから再登録してください。");
}
