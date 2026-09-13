using System.IO;
using System.Text.Json;
using Mcmere.Play.Core;

namespace Mcmere.Play;

public partial class MainWindow
{
    private async Task<object> SmokeDistributionAsync(string url, string? expectedError)
    {
        async Task Click(string text)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var script = "(() => { const b = [...document.querySelectorAll('button')].find(b => b.offsetParent !== null && b.textContent.trim() === " + JsonSerializer.Serialize(text) + " && !b.disabled); if (!b) return false; b.click(); return true; })()";
                if (await Browser.CoreWebView2.ExecuteScriptAsync(script) == "true") return;
                await Task.Delay(100, _lifetime.Token);
            }
            throw new InvalidOperationException("配布検証の操作が見つかりません: " + text);
        }
        async Task Fill(string value)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var script = "(() => { const input = document.querySelector('dialog[open] input'); if (!input) return false; Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(input," + JsonSerializer.Serialize(value) + "); input.dispatchEvent(new Event('input', {bubbles:true})); return true; })()";
                if (await Browser.CoreWebView2.ExecuteScriptAsync(script) == "true") return;
                await Task.Delay(100, _lifetime.Token);
            }
            throw new InvalidOperationException("配布検証の入力欄が見つかりません。");
        }
        var target = DistributionTarget.Parse(url, true);
        var initial = await _application.ViewAsync(_lifetime.Token);
        var saved = initial.Settings.Servers!.SingleOrDefault(server => server.Target == target);
        if (saved is null)
        {
            await Click("サーバーを追加"); await Fill(url); await Click("サーバーを確認"); await Click("このサーバーを追加");
            for (var attempt = 0; attempt < 100; attempt++)
            {
                saved = (await _application.ViewAsync(_lifetime.Token)).Settings.Servers!.SingleOrDefault(server => server.Target == target);
                if (saved is not null) break;
                await Task.Delay(100, _lifetime.Token);
            }
        }
        if (saved is null) throw new InvalidOperationException("サーバーを登録できませんでした。");
        if (saved.PlayerName is null)
        {
            await Click("名前を入力"); await Fill("PlayerName"); await Click("名前を確認する");
        }
        else await Click("環境を確認");
        PlayView view = await _application.ViewAsync(_lifetime.Token);
        for (var attempt = 0; attempt < 300; attempt++)
        {
            view = await _application.ViewAsync(_lifetime.Token);
            var row = view.Servers.Single(server => server.Id == saved.Id);
            if (!view.Busy && (row.ErrorCode is not null || row.Manifest is not null)) break;
            await Task.Delay(100, _lifetime.Token);
        }
        var state = view.Servers.Single(server => server.Id == saved.Id);
        if (expectedError is not null)
        {
            if (state.ErrorCode != expectedError) throw new InvalidOperationException("期待した配布の拒否がありません: " + state.ErrorCode);
            return new { nativeDistributionTested = true, expectedError, rejected = true, realGameConnectionTested = false };
        }
        if (state.ErrorCode is not null || state.Manifest is null || state.Status?.GameState != "offline")
            throw new InvalidOperationException("停止中の合成サーバーの配布情報を取得できません: " + state.Error);
        await Click("環境を準備");
        for (var attempt = 0; attempt < 1200; attempt++)
        {
            view = await _application.ViewAsync(_lifetime.Token); state = view.Servers.Single(server => server.Id == saved.Id);
            if (state.ErrorCode is not null) throw new InvalidOperationException(state.Error);
            if (!view.Busy && state.JavaReady && state.PrismReady && state.Plan?.Changes.Count == 0) break;
            await Task.Delay(100, _lifetime.Token);
        }
        if (view.Busy || !state.JavaReady || !state.PrismReady || state.Plan?.Changes.Count != 0 || view.Activity.GameRunning) throw new InvalidOperationException("配布の準備が完了しませんでした。");
        var manifest = state.Manifest!;
        foreach (var file in manifest.Files)
        {
            var path = PlayFiles.Child(_application.Paths.Game(saved.Id), file.Path);
            if (await PlayFiles.Sha512Async(path, _lifetime.Token) != file.Sha512) throw new InvalidOperationException("配布ファイルが一致しません: " + file.Id);
        }
        var profile = await File.ReadAllTextAsync(PlayFiles.Child(_application.Paths.Instance(saved.Id), "mmc-pack.json"), _lifetime.Token);
        if (!PrismProfile.PackMatches(profile, manifest)) throw new InvalidOperationException("NeoForgeの構成が一致しません。");
        var persisted = view.Settings.Servers!.Single(server => server.Id == saved.Id);
        return new { nativeDistributionTested = true, manifest.ReleaseId, manifest.Sequence, filesVerified = manifest.Files.Count,
            javaVerified = state.JavaReady, prismVerified = state.PrismReady, neoForgeProfileVerified = true,
            keyId = persisted.SigningKey.KeyId, persisted.KeyMinimumSequence, persisted.HighestSequence, realGameConnectionTested = false };
    }
}
