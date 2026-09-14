#requires -Version 5.1
[CmdletBinding()]
param([switch]$Quiet, [switch]$TestMode, [switch]$RemoveData)
$ErrorActionPreference = 'Stop'
$appRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$manifestPath = Join-Path $appRoot 'installation.json'
if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'インストール情報がありません。' }
$installation = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$dataRoot = [IO.Path]::GetFullPath($installation.dataRoot).TrimEnd('\')
$expectedApp = [IO.Path]::GetFullPath((Join-Path $dataRoot 'app')).TrimEnd('\')
if ($installation.product -ne 'mcmere-play' -or $expectedApp -ine $appRoot -or
    [IO.Path]::GetFullPath($installation.appDirectory).TrimEnd('\') -ine $appRoot -or
    $dataRoot -ieq [IO.Path]::GetPathRoot($dataRoot).TrimEnd('\')) { throw '削除対象の保存先を確認できません。' }
if ($TestMode -and $dataRoot -notmatch '[\\/]\.test-data[\\/]') { throw '検証モードには.test-data内の保存先が必要です。' }
$gameRoot = $dataRoot
$pointerPath = Join-Path $dataRoot 'data-location.json'
if ($RemoveData -or !$Quiet) {
    if (Test-Path -LiteralPath $pointerPath) {
        $pointer = Get-Content -LiteralPath $pointerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $gameRoot = [IO.Path]::GetFullPath($pointer.dataRoot).TrimEnd('\')
        $receipt = Get-Content -LiteralPath (Join-Path $gameRoot 'migration-receipt.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($pointer.product -ne 'mcmere-play' -or $pointer.schemaVersion -ne 1 -or $receipt.schemaVersion -ne 1 -or
            $pointer.id -notmatch '^[a-f0-9]{32}$' -or $pointer.id -ne $receipt.id -or
            $pointer.controlRoot -ine $dataRoot -or $receipt.controlRoot -ine $dataRoot -or $receipt.dataRoot -ine $gameRoot -or
            $gameRoot -ieq [IO.Path]::GetPathRoot($gameRoot).TrimEnd('\') -or
            $gameRoot.StartsWith($dataRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $dataRoot.StartsWith($gameRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '移行先の所有情報を確認できません。' }
    }
    if ($TestMode -and $gameRoot -notmatch '[\\/]\.test-data[\\/]') { throw '検証で削除できるのは.test-data内だけです。' }
}
if ($Quiet -and $RemoveData -and !$TestMode) { throw 'データ削除には確認画面が必要です。-Quietを外してください。' }
if (!$Quiet) {
    Add-Type -AssemblyName PresentationFramework
    $window = New-Object Windows.Window
    $window.Title = 'mcmere Play のアンインストール'; $window.Width = 580; $window.SizeToContent = 'Height'; $window.WindowStartupLocation = 'CenterScreen'
    $panel = New-Object Windows.Controls.StackPanel; $panel.Margin = '24'; $window.Content = $panel
    $label = New-Object Windows.Controls.TextBlock; $label.TextWrapping = 'Wrap'; $label.Text = "アプリを削除します。通常はゲームデータと設定を残します。`n`n設置先: $dataRoot`nゲーム保存先: $gameRoot"; $panel.Children.Add($label) | Out-Null
    $check = New-Object Windows.Controls.CheckBox; $check.Margin = '0,20,0,20'; $check.Content = 'ゲーム・セーブ・Prismアカウント・設定も削除する'; $check.IsChecked = [bool]$RemoveData; $panel.Children.Add($check) | Out-Null
    $button = New-Object Windows.Controls.Button; $button.Content = 'アンインストール'; $button.Padding = '12,8'; $button.Add_Click({ $window.DialogResult = $true }); $panel.Children.Add($button) | Out-Null
    if ($window.ShowDialog() -ne $true) { exit 0 }
    $RemoveData = [bool]$check.IsChecked
    if ($RemoveData -and [Windows.MessageBox]::Show("次の保存先のゲーム・セーブ・認証情報と設定を削除します。元に戻せません。`n$dataRoot`n$gameRoot",'データ削除の確認','YesNo','Warning') -ne 'Yes') { exit 0 }
}
try { $appLock = [IO.File]::Open((Join-Path $dataRoot 'application.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
catch { throw 'mcmere Playを終了してから再試行してください。' }
try {
    $setupLock = [IO.File]::Open((Join-Path $dataRoot 'setup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $deleteTargets = @($appRoot)
    if ($RemoveData) {
        foreach ($root in (@($dataRoot,$gameRoot) | Select-Object -Unique)) {
            foreach ($child in @('prism-data','runtimes','cache','staging','backups','state','logs','diagnostics','webview','updates','.setup','settings.json','settings.previous.json','data-location.json','migration-receipt.json','migration-pending.json')) {
                $target = [IO.Path]::GetFullPath((Join-Path $root $child))
                if (!$target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw '削除対象が専用保存先の外にあります。' }
                if (Test-Path -LiteralPath $target) { $deleteTargets += $target }
            }
        }
        foreach ($process in Get-Process) {
            try { $executable = $process.Path } catch { continue }
            if ($executable -and ($executable.StartsWith((Join-Path $gameRoot 'runtimes')+'\',[StringComparison]::OrdinalIgnoreCase) -or $executable.StartsWith((Join-Path $dataRoot 'runtimes')+'\',[StringComparison]::OrdinalIgnoreCase))) { throw 'PrismとMinecraftを終了してからデータを削除してください。' }
        }
    }
    foreach ($target in $deleteTargets) {
        $ancestor = $target
        while ($ancestor) {
            if (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'リンクを含む保存先は削除できません。' }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        if (Test-Path -LiteralPath $target -PathType Container) {
            $scan = New-Object 'Collections.Generic.Stack[string]'; $scan.Push($target)
            while ($scan.Count) {
                foreach ($item in Get-ChildItem -LiteralPath ($scan.Pop()) -Force) {
                    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'リンクを含むデータは削除できません。' }
                    if ($item.PSIsContainer) { $scan.Push($item.FullName) }
                }
            }
        }
    }
    $ancestor = $appRoot
    while ($ancestor) {
        if (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'リンクを含む保存先は削除できません。' }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    $pending = New-Object 'Collections.Generic.Stack[string]'
    $pending.Push($appRoot)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if (([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'リンクを含むアプリフォルダーは削除できません。' }
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'リンクを含むアプリフォルダーは削除できません。' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
    if (!$TestMode) {
        $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\mcmere-play'
        $entry = Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue
        if ($entry -and $entry.InstallLocation -ieq $appRoot) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force }
        $protocol = 'HKCU:\Software\Classes\mcmere-play'
        $commandKey = Join-Path $protocol 'shell\open\command'
        $command = Get-Item -LiteralPath $commandKey -ErrorAction SilentlyContinue
        $exe = Join-Path $appRoot 'mcmere-play.exe'
        if ($command -and $command.GetValue('').StartsWith('"' + $exe + '"',[StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $protocol -Recurse -Force }
        $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'mcmere Play.lnk'
        if (Test-Path -LiteralPath $shortcutPath) {
            Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class McmerePlayShortcutTarget {
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
    }
    public static string Read(string path) {
        object instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true));
        try {
            ((IPersistFile)instance).Load(path, 0);
            var value = new StringBuilder(32768);
            ((IShellLinkW)instance).GetPath(value, value.Capacity, IntPtr.Zero, 4);
            return value.ToString();
        } finally { Marshal.FinalReleaseComObject(instance); }
    }
}
'@
            if ([McmerePlayShortcutTarget]::Read($shortcutPath) -ieq $exe) { Remove-Item -LiteralPath $shortcutPath -Force }
        }
    } else {
        $registration = Join-Path $dataRoot 'registration-test.json'
        if (Test-Path -LiteralPath $registration) { Remove-Item -LiteralPath $registration -Force }
    }
    foreach ($target in ($deleteTargets | Select-Object -Unique)) { Remove-Item -LiteralPath $target -Recurse -Force }
    [pscustomobject]@{ removed = $true; dataPreserved = !$RemoveData; dataRoot = $gameRoot } | ConvertTo-Json
} finally { if ($setupLock) { $setupLock.Dispose() }; $appLock.Dispose() }
