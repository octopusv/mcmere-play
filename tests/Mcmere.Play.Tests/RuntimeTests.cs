using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class RuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "play-runtime-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task RuntimeRepairPreservesOldFilesAndUsesVerifiedArchiveWithoutRedownloading()
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = archive.CreateEntry("bin/java.exe").Open();
            await entry.WriteAsync(new byte[] { 1, 2, 3 });
        }
        var bytes = zip.ToArray();
        var artifact = new RuntimeArtifact("test-runtime", "1", "https://cdn.modrinth.com/runtime.zip",
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length, "bin/java.exe", "https://example.com/license");
        var handler = new ArchiveHandler(bytes);
        using var http = new HttpClient(handler);
        var paths = new PlayPaths(_root);
        var manager = new RuntimeManager(paths, new VerifiedDownloads(http, new DownloadPolicy()));
        var first = await manager.EnsureAsync(artifact, "java");
        Assert.Equal(first, await manager.EnsureAsync(artifact, "java"));
        await File.WriteAllTextAsync(first.Executable, "corrupt");
        var repaired = await manager.EnsureAsync(artifact, "java");
        Assert.NotEqual(first.Directory, repaired.Directory);
        Assert.Equal("corrupt", await File.ReadAllTextAsync(first.Executable));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(repaired.Executable));
        Assert.Equal(repaired, await manager.EnsureAsync(artifact, "java"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void TheServerCannotChooseAnArbitraryJavaDownloadThroughTheCatalog()
    {
        Assert.Equal(RuntimeCatalog.Java21, RuntimeCatalog.Java(new(21, "x64", RuntimeCatalog.Java21.Id)));
        Assert.Throws<DistributionException>(() => RuntimeCatalog.Java(new(21, "x64", "https://untrusted.example/java.zip")));
        Assert.Throws<DistributionException>(() => RuntimeCatalog.Java(new(21, "arm64", RuntimeCatalog.Java21.Id)));
        Assert.Throws<DistributionException>(() => RuntimeCatalog.Java(new(25, "x64", RuntimeCatalog.Java21.Id)));
    }

    private sealed class ArchiveHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
