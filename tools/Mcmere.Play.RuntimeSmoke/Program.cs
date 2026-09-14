using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

if (args.Length is < 1 or > 2 || !Path.IsPathFullyQualified(args[0]))
{
    Console.Error.WriteLine("Usage: runtime-smoke <absolute-isolated-data-directory>");
    return 2;
}
var root = Path.GetFullPath(args[0]);
var paths = new PlayPaths(root);
using var http = VerifiedDownloads.CreateHttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("mcmere-play-runtime-smoke/0.1.0");
var runtimes = new RuntimeManager(paths, new VerifiedDownloads(http, new DownloadPolicy()));
var prism = await runtimes.EnsurePrismAsync();
var java = await runtimes.EnsureJavaAsync(new(21, "x64", RuntimeCatalog.Java21.Id));
var javaCheck = await RuntimeManager.InspectJavaAsync(java.Executable, 21);
var prismCheck = await RuntimeManager.ProbeAsync(prism.Executable, ["--version", "--dir", paths.PrismData]);
if (!prismCheck.Contains("PrismLauncher " + RuntimeCatalog.Prism.Version, StringComparison.Ordinal))
    throw new InvalidOperationException("Unexpected Prism version.");
bool? activityDetected = null;
if (args.Length == 2)
{
    var start = new System.Diagnostics.ProcessStartInfo(java.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
    foreach (var argument in new[] { "-cp", Path.GetFullPath(args[1]), "ActivityProbe" }) start.ArgumentList.Add(argument);
    using var probe = System.Diagnostics.Process.Start(start)!;
    try
    {
        if (await probe.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) != "ready") throw new InvalidOperationException("Activity probe failed.");
        var activity = new ProcessActivity(paths).Read();
        activityDetected = activity.GameRunning && !activity.Uncertain;
        if (activityDetected != true) throw new InvalidOperationException("Owned Java process was not detected: " + activity.Detail);
    }
    finally { await probe.StandardInput.WriteLineAsync("exit"); await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
}
var result = new { prismVersion = prismCheck.Trim(), javaMajor = javaCheck.Major, javaArchitecture = javaCheck.Architecture,
    javaVersion = javaCheck.VersionLine, artifactChecksumsVerified = true, gameLoginOrConnectionTested = false };
await PlayFiles.WriteAtomicAsync(PlayFiles.Child(root, "runtime-smoke-result.json"), DistributionJson.Bytes(result));
if (activityDetected is not null) await PlayFiles.WriteAtomicAsync(PlayFiles.Child(root, "activity-smoke-result.json"), DistributionJson.Bytes(new { activityDetected }));
Console.WriteLine(System.Text.Encoding.UTF8.GetString(DistributionJson.Bytes(result)));
return 0;
