using System.Text;
using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

namespace Mcmere.Play.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "play-diagnostics-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    [Fact]
    public void LogsRotateAtTheConfiguredBoundAndNeverStoreUnknownText()
    {
        var paths = new PlayPaths(_root);
        var log = new DiagnosticLog(paths, 512, 3);
        for (var index = 0; index < 40; index++) log.Write(DiagnosticEvent.OperationFailed, "C:/Users/PrivateName/Bearer-secret");
        log.Write(DiagnosticEvent.OperationFailed, "settings_corrupt");
        var files = Directory.GetFiles(Path.Combine(_root, "diagnostics", "logs"));
        Assert.Equal(3, files.Length);
        Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1, 512));
        Assert.All(files, path => Assert.DoesNotContain("PrivateName", File.ReadAllText(path)));
        var entries = log.Read();
        Assert.NotEmpty(entries);
        Assert.Equal("settings_corrupt", entries.Last().ErrorCode);
        Assert.Contains(entries, entry => entry.ErrorCode == "unrecognized_error");
    }
    [Fact]
    public void ExportRevalidatesStoredEntriesAndOmitsUnexpectedFields()
    {
        var paths = new PlayPaths(_root);
        var log = new DiagnosticLog(paths);
        log.Write(DiagnosticEvent.Started);
        File.AppendAllText(Path.Combine(_root, "diagnostics", "logs", "play-0.jsonl"), "{\"time\":\"2026-09-14T00:00:00Z\",\"event\":\"operationFailed\",\"errorCode\":\"PrivateName\",\"token\":\"secret\"}\ninvalid json\n");
        var text = Encoding.UTF8.GetString(DistributionJson.Bytes(log.Read()));
        Assert.DoesNotContain("PrivateName", text);
        Assert.DoesNotContain("secret", text);
    }
    [Fact]
    public void ReportUsesFreshOpaqueIdsAndDoesNotExportAccountNamesOrPaths()
    {
        var change = new SyncItem("game", "mods/PrivateName.jar", SyncAction.Add, null, 0, null, 1, "private-id");
        var plan = new SyncPlan("private-plan", "private-server", "private-release", [change], [], 0, []);
        var view = new PlayView(new PlaySettings(), [new ServerView("private-server", Error: "Bearer secret", ErrorCode: "PrivateName", Plan: plan, Directory: "C:/Users/PrivateName")], false, false, new(false, false, false), "0.1.0");
        string Report() => Encoding.UTF8.GetString(DistributionJson.Bytes(Diagnostics.Create(view, [new("java", "21.0.12.1+1-LTS", true)])));
        var first = Report(); var second = Report();
        Assert.NotEqual(first, second);
        foreach (var value in new[] { "PrivateName", "private-server", "private-plan", "Bearer", "secret", "mods/", "C:/Users" }) Assert.DoesNotContain(value, first);
        using var document = System.Text.Json.JsonDocument.Parse(first);
        Assert.Equal("21.0.12.1+1-LTS", document.RootElement.GetProperty("runtimes")[0].GetProperty("version").GetString());
    }
}
