using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record TransferProgress(string FileId, long Received, long Total);
public sealed record DownloadSpec(string Id, string Url, long Length, string Hash, string Algorithm = "sha512");
internal sealed record PartialDownload(string Url, long Length, string? ETag);

public sealed class DownloadPolicy(Uri? gateway = null, bool development = false)
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
        "api.adoptium.net", "aka.ms", "go.microsoft.com", "msedge.sf.dl.delivery.mp.microsoft.com"
    };
    public bool IsGateway(Uri uri) => gateway is not null && uri.GetLeftPart(UriPartial.Authority).Equals(gateway.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    public void Validate(Uri uri)
    {
        var local = development && uri.Scheme == "http" && IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip) && IsGateway(uri);
        if (!local && (uri.Scheme != "https" || !uri.IsDefaultPort)) Fail();
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || (!IsGateway(uri) && !Hosts.Contains(uri.IdnHost))) Fail();
    }
    private static void Fail() => throw new DistributionException("download_origin", "この配布元からの取得は許可されていません。");
}

public sealed class VerifiedDownloads(HttpClient http, DownloadPolicy policy)
{
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };

    public async Task<string> GetAsync(DownloadSpec spec, string cacheRoot, string? sessionToken = null,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var algorithm = spec.Algorithm switch { "sha512" => HashAlgorithmName.SHA512, "sha256" => HashAlgorithmName.SHA256,
            _ => throw new DistributionException("invalid_hash", "対応しないハッシュ方式です。") };
        ManifestValidation.Hash(spec.Hash, spec.Algorithm == "sha512" ? 128 : 64);
        if (spec.Length is < 1 or > 2L * 1024 * 1024 * 1024) throw new DistributionException("invalid_size", "ファイルのサイズが不正です。");
        var initial = new Uri(spec.Url, UriKind.Absolute);
        policy.Validate(initial);
        Directory.CreateDirectory(cacheRoot);
        var destination = PlayFiles.Child(cacheRoot, spec.Algorithm + "-" + spec.Hash);
        using (await CacheAccess.AcquireAsync(destination, ct))
        {
            if (File.Exists(destination))
            {
                if (await MatchesAsync(destination, spec, algorithm, ct)) { progress?.Report(new(spec.Id, spec.Length, spec.Length)); return destination; }
                File.Delete(destination);
            }
            var partial = destination + ".partial";
            var checkpoint = partial + ".json";
            PlayFiles.NoLinksToRoot(partial); PlayFiles.NoLinksToRoot(checkpoint);
            PartialDownload? previous = null;
            if (File.Exists(checkpoint) && new FileInfo(checkpoint).Length < 16384)
            {
                try { previous = DistributionJson.Read<PartialDownload>(await File.ReadAllBytesAsync(checkpoint, ct)); }
                catch (Exception error) when (error is System.Text.Json.JsonException or DistributionException) { }
            }
            var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset > spec.Length || previous?.Url != initial.AbsoluteUri || previous.Length != spec.Length ||
                previous.ETag is null || !EntityTagHeaderValue.TryParse(previous.ETag, out var parsedTag) || parsedTag.IsWeak)
                offset = 0;
            if (offset == spec.Length && await MatchesAsync(partial, spec, algorithm, ct))
            {
                File.Move(partial, destination); if (File.Exists(checkpoint)) File.Delete(checkpoint); return destination;
            }
            if (offset == spec.Length) offset = 0;
            using var response = await OpenAsync(initial, offset, previous?.ETag, sessionToken, ct);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
            {
                if (File.Exists(partial)) File.Delete(partial);
                if (File.Exists(checkpoint)) File.Delete(checkpoint);
                throw new DistributionException("download_restart", "取得情報が変わりました。もう一度お試しください。", true);
            }
            if (!response.IsSuccessStatusCode)
                throw new DistributionException("download_failed", "ファイルを取得できません (HTTP " + (int)response.StatusCode + ")。", true);
            var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range?.From != offset || range.Length != spec.Length || range.To != spec.Length - 1 ||
                    (append && response.Headers.ETag?.ToString() != previous!.ETag))
                    throw new DistributionException("download_range", "再開するファイルの範囲を確認できません。", true);
            }
            if (!append) offset = 0;
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != spec.Length - offset)
                throw new DistributionException("download_size", "取得するファイルのサイズが一致しません。");
            var tag = response.Headers.ETag;
            var metadata = new PartialDownload(initial.AbsoluteUri, spec.Length, tag is { IsWeak: false } ? tag.ToString() : null);
            await PlayFiles.WriteAtomicAsync(checkpoint, DistributionJson.Bytes(metadata), ct);
            await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[131072];
                long received = offset;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (count > spec.Length - received) throw new DistributionException("download_size", "取得したファイルが予定サイズを超えています。");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    received += count;
                    progress?.Report(new(spec.Id, received, spec.Length));
                }
                await output.FlushAsync(ct);
                if (received != spec.Length) throw new DistributionException("download_incomplete", "ファイルの取得が中断されました。", true);
            }
            if (!await MatchesAsync(partial, spec, algorithm, ct))
            {
                File.Delete(partial); File.Delete(checkpoint);
                throw new DistributionException("download_corrupt", "取得したファイルのハッシュが一致しません。", true);
            }
            PlayFiles.NoLinksToRoot(destination);
            File.Move(partial, destination);
            File.Delete(checkpoint);
            return destination;
        }
    }

    private async Task<HttpResponseMessage> OpenAsync(Uri initial, long offset, string? etag, string? token, CancellationToken ct)
    {
        var uri = initial;
        var sendToken = token is not null && policy.IsGateway(initial);
        for (var hop = 0; hop < 6; hop++)
        {
            policy.Validate(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (sendToken) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag!));
            }
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.RequestMessage?.RequestUri is { } effective && effective != uri)
            {
                response.Dispose();
                throw new DistributionException("automatic_redirect", "取得用HTTPクライアントの設定が不正です。");
            }
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new DistributionException("download_redirect", "配布元の転送先がありません。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                sendToken &= policy.IsGateway(uri);
                continue;
            }
            return response;
        }
        throw new DistributionException("download_redirect", "配布元の転送回数が上限を超えました。");
    }

    private static async Task<bool> MatchesAsync(string path, DownloadSpec spec, HashAlgorithmName algorithm, CancellationToken ct)
    {
        PlayFiles.NoLinksToRoot(path);
        if (new FileInfo(path).Length != spec.Length) return false;
        await using var file = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = new byte[131072];
        int count;
        while ((count = await file.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, count);
        return Convert.ToHexString(hash.GetHashAndReset()).Equals(spec.Hash, StringComparison.OrdinalIgnoreCase);
    }
}
