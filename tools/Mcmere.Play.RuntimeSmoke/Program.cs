using Mcmere.Distribution.Contracts;
using Mcmere.Play.Core;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
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
var result = new { prismVersion = prismCheck.Trim(), javaMajor = javaCheck.Major, javaArchitecture = javaCheck.Architecture,
    javaVersion = javaCheck.VersionLine, artifactChecksumsVerified = true, gameLoginOrConnectionTested = false };
await PlayFiles.WriteAtomicAsync(PlayFiles.Child(root, "runtime-smoke-result.json"), DistributionJson.Bytes(result));
Console.WriteLine(System.Text.Encoding.UTF8.GetString(DistributionJson.Bytes(result)));
return 0;
