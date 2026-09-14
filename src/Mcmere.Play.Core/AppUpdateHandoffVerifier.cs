using System.Diagnostics;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public static class AppUpdateHandoffVerifier
{
    public static async Task<(AppUpdateHandoff Handoff, AppUpdateManifest Manifest)> VerifyAsync(string requestFile, PlayPaths paths,
        string executable, DistributionKey? trustedKey = null, CancellationToken ct = default)
    {
        if (!Path.GetFullPath(requestFile).Equals(AppUpdater.HandoffPath(paths), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(requestFile) || new FileInfo(requestFile).Length > AppUpdateSigning.MaximumEnvelopeBytes + 2048)
            throw new DistributionException("update_handoff", "アプリ更新の引き継ぎ情報が不正です。");
        PlayFiles.NoLinksToRoot(requestFile);
        var handoff = DistributionJson.Read<AppUpdateHandoff>(await File.ReadAllBytesAsync(requestFile, ct));
        var manifest = AppUpdateSigning.Verify(handoff.Envelope, trustedKey ?? AppUpdateCatalog.TrustedKey);
        if (!AppUpdateCatalog.IsVersion(handoff.FromVersion) || Version.Parse(manifest.Version) <= Version.Parse(handoff.FromVersion) ||
            handoff.ParentProcessId <= 0 || !Path.GetFullPath(executable).Equals(AppUpdater.SetupPath(paths, manifest), StringComparison.OrdinalIgnoreCase) ||
            !await AppUpdater.VerifySetupAsync(executable, manifest, ct))
            throw new DistributionException("update_handoff", "更新対象のセットアップを確認できません。");
        return (handoff, manifest);
    }
    public static async Task WaitForParentAsync(AppUpdateHandoff handoff, CancellationToken ct = default)
    {
        if (handoff.ParentProcessId == Environment.ProcessId) throw new DistributionException("update_parent", "更新を開始したアプリが一致しません。");
        Process parent;
        try { parent = Process.GetProcessById(handoff.ParentProcessId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            try
            {
                if (parent.HasExited || Math.Abs((new DateTimeOffset(parent.StartTime.ToUniversalTime()) - handoff.ParentStartedAt).TotalMilliseconds) > 1) return;
                await parent.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
            }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { throw new DistributionException("application_running", "mcmere Playが終了していません。終了してから更新を再試行してください。"); }
        }
    }
}
