using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public enum DiagnosticEvent { Started, OperationCompleted, OperationFailed, StateFailed, MigrationCompleted }
public sealed record DiagnosticEntry(DateTimeOffset Time, DiagnosticEvent Event, string ErrorCode);
public sealed record RuntimeDiagnostic(string Kind, string? Version, bool Verified, string? ErrorCode = null);

public sealed class DiagnosticLog
{
    private readonly string _root;
    private readonly int _limit;
    private readonly int _count;
    private readonly object _gate = new();
    public DiagnosticLog(PlayPaths paths, int maximumBytes = 5 * 1024 * 1024, int maximumFiles = 10)
    {
        if (maximumBytes is < 256 or > 5 * 1024 * 1024 || maximumFiles is < 1 or > 10) throw new ArgumentOutOfRangeException();
        _root = PlayFiles.Child(paths.ControlRoot, "diagnostics/logs"); _limit = maximumBytes; _count = maximumFiles;
    }
    public void Write(DiagnosticEvent kind, string? code = null)
    {
        if (!Enum.IsDefined(kind)) return;
        lock (_gate)
        {
            try
            {
                PlayFiles.NoLinksToRoot(_root); Directory.CreateDirectory(_root);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new DiagnosticEntry(DateTimeOffset.UtcNow, kind, Diagnostics.ErrorCode(code)), new JsonSerializerOptions(DistributionJson.Options) { WriteIndented = false });
                var active = FileAt(0);
                if (File.Exists(active) && new FileInfo(active).Length + bytes.Length + 1 > _limit)
                {
                    var last = FileAt(_count - 1);
                    if (File.Exists(last)) File.Delete(last);
                    for (var index = _count - 2; index >= 0; index--)
                        if (File.Exists(FileAt(index))) File.Move(FileAt(index), FileAt(index + 1));
                }
                using var stream = new FileStream(active, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes); stream.WriteByte(10);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DistributionException) { }
        }
    }
    private string FileAt(int index) => PlayFiles.Child(_root, "play-" + index + ".jsonl");
    public IReadOnlyList<DiagnosticEntry> Read()
    {
        lock (_gate)
        {
            var entries = new List<DiagnosticEntry>();
            try
            {
                for (var index = _count - 1; index >= 0; index--)
                {
                    var path = FileAt(index);
                    if (!File.Exists(path) || new FileInfo(path).Length > _limit) continue;
                    foreach (var line in File.ReadLines(path))
                    {
                        if (line.Length > 512) continue;
                        try
                        {
                            var entry = DistributionJson.Read<DiagnosticEntry>(Encoding.UTF8.GetBytes(line));
                            if (entry is not null && Enum.IsDefined(entry.Event)) entries.Add(entry with { ErrorCode = Diagnostics.ErrorCode(entry.ErrorCode) });
                        }
                        catch (JsonException) { }
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DistributionException) { }
            return entries;
        }
    }
}

public static class Diagnostics
{
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "operation_failed", "operation_busy", "settings_corrupt", "settings_version", "invalid_request", "unknown_operation",
        "invalid_runtime", "runtime_catalog_required", "java_mismatch", "probe_failed", "setup_required", "launch_failed",
        "download_failed", "download_hash", "hash_mismatch", "invalid_signature", "invalid_manifest", "not_whitelisted",
        "session_expired", "server_not_found", "manifest_changed", "insufficient_space", "game_running", "prism_running",
        "migration_busy", "migration_changed", "migration_corrupt", "migration_state", "migration_destination",
        "update_failed", "update_signature", "update_hash", "cancelled", "unrecognized_error"
    };
    public static string ErrorCode(string? value) => string.IsNullOrEmpty(value) ? "" : Codes.Contains(value) ? value : "unrecognized_error";
    public static string? Version(string? value) => value is not null && Regex.IsMatch(value, "^[0-9][0-9a-zA-Z.+_-]{0,63}$") ? value : null;
    public static object Create(PlayView state, IReadOnlyList<RuntimeDiagnostic> runtimes)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        string Id(string value) => Convert.ToHexString(HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
        return new
        {
            version = PlayVersion.Current, operatingSystem = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), processorCount = Environment.ProcessorCount,
            runtimes = runtimes.Select(item => new { kind = item.Kind is "java" or "prism" ? item.Kind : "unknown", version = Version(item.Version), item.Verified, errorCode = ErrorCode(item.ErrorCode) }),
            servers = state.Servers.Select(server => new
            {
                id = Id(server.Id), errorCode = ErrorCode(server.ErrorCode), minecraft = Version(server.Manifest?.MinecraftVersion),
                neoForge = Version(server.Manifest?.Loader.Version), java = server.Manifest?.Java.Major,
                server.JavaReady, server.PrismReady,
                changes = server.Plan?.Changes.Select(item => new { id = Id(server.Id + "/" + item.Scope + "/" + item.Path), action = Enum.IsDefined(item.Action) ? item.Action.ToString() : "unknown" }).ToArray()
            }).ToArray()
        };
    }
}
