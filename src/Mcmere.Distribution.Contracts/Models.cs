using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcmere.Distribution.Contracts;

public static class DistributionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);
    public static T Read<T>(ReadOnlySpan<byte> bytes) => JsonSerializer.Deserialize<T>(bytes, Options)
        ?? throw new DistributionException("invalid_document", "配布情報を読み取れません。");
}

public sealed class DistributionException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public enum FileRequirement { Required, Recommended }
public enum FileUpdatePolicy { Managed, Seed }
public enum FileSourceKind { Modrinth, Direct, Hosted, Manual }

public sealed record FileSource
{
    public FileSourceKind Kind { get; init; }
    public string? Url { get; init; }
    public string? ProjectId { get; init; }
    public string? VersionId { get; init; }
    public string? BlobId { get; init; }
    public string? PageUrl { get; init; }
}

public sealed record PackFile
{
    public required string Id { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public long Length { get; init; }
    public required string Sha512 { get; init; }
    public required FileSource Source { get; init; }
    public FileRequirement Requirement { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public FileUpdatePolicy UpdatePolicy { get; init; }
    public IReadOnlyList<string> ModIds { get; init; } = [];
    public IReadOnlyList<string> Requires { get; init; } = [];
}

public sealed record JavaRequirement(int Major, string Architecture, string RuntimeCatalogId);
public sealed record LoaderRequirement(string Kind, string Version);

public sealed record PackManifest
{
    public int SchemaVersion { get; init; } = 1;
    public int FingerprintVersion { get; init; } = 1;
    public string? DeploymentId { get; init; }
    public IReadOnlyList<string> RequiredFeatures { get; init; } = [];
    public IReadOnlyList<ResourcePackSelection> ResourcePacks { get; init; } = [];
    public IReadOnlyList<PackPair> PackPairs { get; init; } = [];
    public required string ReleaseId { get; init; }
    public long Sequence { get; init; }
    public required string ServerPublicId { get; init; }
    public required string ServerName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public required string DisplayVersion { get; init; }
    public string Changelog { get; init; } = "";
    public string MinimumPlayVersion { get; init; } = "0.1.0";
    public string PrismVersion { get; init; } = "11.1.0";
    public required string MinecraftVersion { get; init; }
    public required LoaderRequirement Loader { get; init; }
    public required JavaRequirement Java { get; init; }
    public int RecommendedMemoryMiB { get; init; } = 4096;
    public required string GameEndpoint { get; init; }
    public required string ServerFingerprint { get; init; }
    public required string ClientFingerprint { get; init; }
    public IReadOnlyList<PackFile> Files { get; init; } = [];
}

public sealed record ResourcePackSelection(string BindingId, string FileId);
public sealed record PackPair(string BindingId, string Mode, string ServerArtifactSha512, string ClientFileId);

public sealed record SignedManifest(string KeyId, string Payload, string Sha256, string Signature);
public sealed record DistributionKey(string KeyId, string PublicKey);
public sealed record ServerInfo(string PublicId, string Name, int SchemaVersion, DistributionKey SigningKey, IReadOnlyList<SignedKeyTransition>? KeyTransitions = null,
    IReadOnlyList<int>? SupportedManifestSchemas = null, int RequiredManifestSchema = 1);
public sealed record NameRequest(string PlayerName);
public sealed record DistributionSession(string PlayerName, string SessionToken, DateTimeOffset ExpiresAt, ServerInfo Server);
public sealed record ReleaseStatus(string ReleaseId, long Sequence, string DistributionState, string GameState,
    string CompatibilityState, DateTimeOffset ObservedAt);
public sealed record LaunchCheckRequest(string ReleaseId, IReadOnlyList<string>? SupportedFeatures = null);
public sealed record LaunchCheckResult(bool Allowed, string ReleaseId, string? Reason);
public sealed record DistributionError(string Code, string Message, bool Retryable, string RequestId);

public sealed record RuntimeArtifact(string Id, string Version, string Url, string Sha256, long Length,
    string Executable, string LicenseUrl);
