using System.Net;
using System.Net.Http.Headers;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record DistributionTarget(string Origin, string PublicId)
{
    public Uri BaseUri => new(Origin.TrimEnd('/') + "/");
    public static DistributionTarget Parse(string input, bool development = false)
    {
        if (input.Length > 4096) throw new DistributionException("invalid_url", "配布ページのURLが長すぎます。");
        if (input.StartsWith("mcmere-play:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(input, UriKind.Absolute, out var protocol) || protocol.Host != "add" || protocol.AbsolutePath is not ("" or "/") ||
                !string.IsNullOrEmpty(protocol.Fragment) || !string.IsNullOrEmpty(protocol.UserInfo) || !protocol.Query.StartsWith("?url=", StringComparison.Ordinal) || protocol.Query.Contains('&'))
                throw new DistributionException("invalid_url", "アプリで開くためのURLが不正です。");
            input = Uri.UnescapeDataString(protocol.Query[5..]);
        }
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || !uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal))
            throw new DistributionException("invalid_url", "管理者から届いた配布ページのURLを入力してください。");
        var local = development && uri.Scheme == "http" && IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip);
        if (!local && (uri.Scheme != "https" || !uri.IsDefaultPort))
            throw new DistributionException("invalid_url", "配布ページにはHTTPSのURLを指定してください。");
        var id = uri.AbsolutePath[3..];
        ManifestValidation.Id(id);
        return new(uri.GetLeftPart(UriPartial.Authority), id);
    }
}

public sealed record FetchedRelease(ReleaseStatus Status, SignedManifest Envelope, PackManifest Manifest);

public sealed class DistributionClient(HttpClient http, DistributionTarget target, DistributionKey? trustedKey = null, TimeProvider? time = null)
{
    private DistributionSession? _session;
    private string? _name;
    private DistributionKey? _trustedKey = trustedKey;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    public DistributionTarget Target => target;
    public string? PlayerName => _session?.PlayerName;
    public string? SessionToken => _session?.SessionToken;

    public async Task<ServerInfo> DiscoverAsync(CancellationToken ct = default)
    {
        var info = await SendAsync<ServerInfo>(HttpMethod.Get, "info", null, null, ct);
        if (info.PublicId != target.PublicId || info.SchemaVersion != 1 || info.SigningKey is null || string.IsNullOrWhiteSpace(info.Name) || info.Name.Length > 100)
            throw new DistributionException("invalid_server", "配布先の情報を確認できません。");
        using var key = System.Security.Cryptography.RSA.Create();
        try
        {
            var bytes = Convert.FromBase64String(info.SigningKey.PublicKey);
            if (bytes.Length > 16384) throw new FormatException();
            key.ImportSubjectPublicKeyInfo(bytes, out var read);
            if (read != bytes.Length || key.KeySize < 2048 || ManifestSigning.PublicKey(key).KeyId != info.SigningKey.KeyId) throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        { throw new DistributionException("invalid_key", "配布元の公開鍵を確認できません。"); }
        if (_trustedKey is not null && _trustedKey != info.SigningKey)
            throw new DistributionException("signing_key_changed", "配布元の公開鍵が変更されました。管理者に確認してください。");
        return info;
    }

    public void Trust(DistributionKey key)
    {
        if (_trustedKey is not null && _trustedKey != key) throw new DistributionException("signing_key_changed", "保存済みの公開鍵を上書きできません。");
        _trustedKey = key;
    }

    public async Task<DistributionSession> IdentifyAsync(string name, CancellationToken ct = default)
    {
        _name = ManifestValidation.Name(name);
        _session = null;
        return await SessionAsync(ct);
    }

    private async Task<DistributionSession> SessionAsync(CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_session is not null && _session.ExpiresAt > _time.GetUtcNow().AddSeconds(20)) return _session;
            if (_name is null) throw new DistributionException("name_required", "Minecraftの名前を入力してください。");
            var session = await SendAsync<DistributionSession>(HttpMethod.Post, "sessions", new NameRequest(_name), null, ct);
            if (session.PlayerName is null || session.Server is null || session.SessionToken is null || !session.PlayerName.Equals(_name, StringComparison.OrdinalIgnoreCase) || session.Server.PublicId != target.PublicId ||
                session.ExpiresAt <= _time.GetUtcNow() || session.ExpiresAt > _time.GetUtcNow().AddMinutes(16) ||
                session.SessionToken.Length is < 32 or > 256 || session.SessionToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new DistributionException("invalid_session", "配布の利用確認を完了できません。");
            if (_trustedKey is null || session.Server.SigningKey != _trustedKey)
                throw new DistributionException("signing_key_changed", "配布元の公開鍵が一致しません。");
            _session = session;
            return session;
        }
        finally { _sessionGate.Release(); }
    }

    public async Task<FetchedRelease> FetchAsync(long minimumSequence = 1, CancellationToken ct = default)
    {
        if (_trustedKey is null) throw new DistributionException("untrusted_server", "先に配布元を確認してください。");
        var current = await AuthorizedAsync<ReleaseStatus>(HttpMethod.Get, "current", null, ct);
        ManifestValidation.Id(current.ReleaseId);
        var envelope = await AuthorizedAsync<SignedManifest>(HttpMethod.Get, "releases/" + current.ReleaseId, null, ct);
        var manifest = ManifestSigning.Verify(envelope, _trustedKey, target.PublicId, minimumSequence);
        if (manifest.ReleaseId != current.ReleaseId || manifest.Sequence != current.Sequence)
            throw new DistributionException("release_changed", "配布内容が更新されました。もう一度確認してください。", true);
        if (!Version.TryParse(manifest.MinimumPlayVersion, out var required) || required > Version.Parse(PlayVersion.Current))
            throw new DistributionException("application_update_required", "この配布版にはアプリの更新が必要です。");
        return new(current, envelope, manifest);
    }

    public async Task<LaunchCheckResult> CheckLaunchAsync(string releaseId, CancellationToken ct = default)
    {
        ManifestValidation.Id(releaseId);
        var result = await AuthorizedAsync<LaunchCheckResult>(HttpMethod.Post, "launch-check", new LaunchCheckRequest(releaseId), ct);
        if (result.ReleaseId != releaseId) throw new DistributionException("release_changed", "配布内容が更新されました。もう一度確認してください。", true);
        return result;
    }

    public async Task<string> DownloadAsync(PackFile file, VerifiedDownloads downloads, string cacheRoot,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        if (file.Source.Kind == FileSourceKind.Manual) throw new DistributionException("manual_download", file.Name + "は配布ページからの取得が必要です。");
        var session = await SessionAsync(ct);
        var url = file.Source.Kind == FileSourceKind.Hosted
            ? new Uri(target.BaseUri, "v1/servers/" + target.PublicId + "/files/" + Uri.EscapeDataString(file.Id)).AbsoluteUri
            : file.Source.Url!;
        return await downloads.GetAsync(new(file.Id, url, file.Length, file.Sha512), cacheRoot,
            file.Source.Kind == FileSourceKind.Hosted ? session.SessionToken : null, progress, ct);
    }

    private async Task<T> AuthorizedAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = await SessionAsync(ct);
            try { return await SendAsync<T>(method, path, body, session.SessionToken, ct); }
            catch (DistributionException error) when (error.Code == "session_expired" && attempt == 0) { _session = null; }
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        var uri = new Uri(target.BaseUri, "v1/servers/" + target.PublicId + "/" + path);
        using var request = new HttpRequestMessage(method, uri);
        if (body is not null) { request.Content = new ByteArrayContent(DistributionJson.Bytes(body)); request.Content.Headers.ContentType = new("application/json"); }
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } actual && actual != uri)
            throw new DistributionException("unexpected_redirect", "配布APIの転送先は利用できません。");
        var maximum = response.IsSuccessStatusCode ? 8 * 1024 * 1024 : 65536;
        if (response.Content.Headers.ContentLength > maximum) throw new DistributionException("response_too_large", "配布APIの応答が大きすぎます。");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[32768]; int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + count > maximum) throw new DistributionException("response_too_large", "配布APIの応答が大きすぎます。");
            bytes.Write(buffer, 0, count);
        }
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                var error = DistributionJson.Read<DistributionError>(bytes.ToArray());
                if (error.Code.Length > 100 || error.Message.Length > 2000) throw new System.Text.Json.JsonException();
                throw new DistributionException(error.Code, error.Message, error.Retryable);
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or NullReferenceException)
            { throw new DistributionException("distribution_unavailable", "配布情報を確認できません (HTTP " + (int)response.StatusCode + ")。", true); }
        }
        try { return DistributionJson.Read<T>(bytes.ToArray()); }
        catch (System.Text.Json.JsonException) { throw new DistributionException("invalid_response", "配布APIの応答を読み取れません。"); }
    }
}
