using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "play-downloads-" + Guid.NewGuid().ToString("N"));
    public DownloadTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task VerifiedCacheAvoidsASecondNetworkDownload()
    {
        var bytes = new byte[] { 3, 4, 5 };
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }));
        var downloads = new VerifiedDownloads(http, new DownloadPolicy());
        var spec = Spec(bytes);
        var first = await downloads.GetAsync(spec, _root);
        var second = await downloads.GetAsync(spec, _root);
        Assert.Equal(first, second); Assert.Equal(1, calls); Assert.Equal(bytes, await File.ReadAllBytesAsync(first));
    }
    [Fact]
    public async Task IndependentClientsShareOneVerifiedCacheWriterAndCancelledWaitersDoNotBlockIt()
    {
        var bytes = new byte[] { 2, 4, 6, 8 };
        var handler = new PausedHandler(bytes);
        using var http = new HttpClient(handler);
        var spec = Spec(bytes);
        var first = new VerifiedDownloads(http, new DownloadPolicy()).GetAsync(spec, _root);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancel = new CancellationTokenSource();
        var cancelled = new VerifiedDownloads(http, new DownloadPolicy()).GetAsync(spec, _root, ct: cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var second = new VerifiedDownloads(http, new DownloadPolicy()).GetAsync(spec with { Id = "second" }, _root);
        handler.Resume.SetResult();
        var paths = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(paths[0], paths[1]);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(paths[0]));
    }
    [Fact]
    public async Task LocalReuseAndADownloadCannotReplaceTheSameContentConcurrently()
    {
        var bytes = new byte[] { 2, 4, 6, 8 };
        var paths = new PlayPaths(_root);
        var reuse = Path.Combine(_root, "selected-mods");
        Directory.CreateDirectory(reuse);
        await File.WriteAllBytesAsync(Path.Combine(reuse, "test.jar"), bytes);
        var spec = Spec(bytes);
        var handler = new PausedHandler(bytes);
        using var http = new HttpClient(handler);
        var downloads = new VerifiedDownloads(http, new DownloadPolicy());
        var first = downloads.GetAsync(spec, paths.Cache);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var client = new DistributionClient(http, new("https://packs.example", new string('2', 32)));
        var file = Fixture.File("test") with { Length = bytes.Length, Sha512 = spec.Hash };
        var reuseTask = new ClientFileProvider(client, downloads, paths, [reuse]).GetAsync(file, default);
        handler.Resume.SetResult();
        var completed = await Task.WhenAll(first, reuseTask).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(completed[0], completed[1]);
        Assert.Equal(1, handler.Calls);
        Assert.Single(Directory.GetFiles(paths.Cache));
    }
    private sealed class PausedHandler(byte[] bytes) : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult();
            await Resume.Task.WaitAsync(ct);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
    }
    [Fact]
    public async Task CorruptBytesNeverBecomeACompletedCacheEntry()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([8, 8, 8]) }));
        var downloads = new VerifiedDownloads(http, new DownloadPolicy());
        var error = await Assert.ThrowsAsync<DistributionException>(() => downloads.GetAsync(Spec([1, 2, 3]), _root));
        Assert.Equal("download_corrupt", error.Code);
        Assert.Empty(Directory.GetFiles(_root));
    }
    [Fact]
    public async Task AuthorizationDoesNotFollowRedirectsToThePublicCdn()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal("secret", request.Headers.Authorization?.Parameter);
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new("https://cdn.modrinth.com/example.jar"); return redirect;
            }
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        await new VerifiedDownloads(http, new DownloadPolicy(new Uri("https://packs.example"))).GetAsync(Spec(bytes) with { Url = "https://packs.example/file" }, _root, "secret");
        Assert.Equal(2, calls);
    }
    [Fact]
    public async Task AnUnapprovedRedirectIsRejectedBeforeSendingTheRequest()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://127.0.0.1/secrets"); return response;
        }));
        Assert.Equal("download_origin", (await Assert.ThrowsAsync<DistributionException>(() => new VerifiedDownloads(http, new DownloadPolicy()).GetAsync(Spec([1]), _root))).Code);
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task APartialFileResumesOnlyWithMatchingStrongEtagAndRange()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var spec = Spec(bytes);
        var path = Path.Combine(_root, "sha512-" + spec.Hash + ".partial");
        await File.WriteAllBytesAsync(path, [1, 2]);
        await File.WriteAllBytesAsync(path + ".json", DistributionJson.Bytes(new { url = spec.Url, length = 5, eTag = "\"stable\"" }));
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal(2, request.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"stable\"", request.Headers.IfRange!.EntityTag!.Tag);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([3, 4, 5]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 4, 5);
            response.Headers.ETag = new("\"stable\""); return response;
        }));
        var result = await new VerifiedDownloads(http, new DownloadPolicy()).GetAsync(spec, _root);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result));
    }
    [Fact]
    public async Task ServerIgnoringRangeRestartsTheFileWithoutAppendingWrongBytes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var spec = Spec(bytes);
        var path = Path.Combine(_root, "sha512-" + spec.Hash + ".partial");
        await File.WriteAllBytesAsync(path, [9]);
        await File.WriteAllBytesAsync(path + ".json", DistributionJson.Bytes(new { url = spec.Url, length = 3, eTag = "\"old\"" }));
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        var result = await new VerifiedDownloads(http, new DownloadPolicy()).GetAsync(spec, _root);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(result));
    }
    [Fact]
    public async Task InvalidArchiveIsRejectedBeforeExtractingAnyEntry()
    {
        var zip = Path.Combine(_root, "unsafe.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("okay.txt"); archive.CreateEntry("../outside.txt");
        }
        var output = Path.Combine(_root, "extract");
        await Assert.ThrowsAsync<DistributionException>(() => SafeArchive.ExtractAsync(zip, output));
        Assert.False(Directory.Exists(output));
    }
    private static DownloadSpec Spec(byte[] bytes) => new("test", "https://cdn.modrinth.com/example.jar", bytes.Length,
        Convert.ToHexString(SHA512.HashData(bytes)).ToLowerInvariant());
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
