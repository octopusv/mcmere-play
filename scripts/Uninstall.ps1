#requires -Version 5.1
[CmdletBinding()]
param([switch]$Quiet, [switch]$TestMode)
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
if (!$Quiet) {
    Add-Type -AssemblyName PresentationFramework
    $answer = [Windows.MessageBox]::Show('mcmere Playを削除します。ゲームデータと設定は保持します。続行しますか？','mcmere Play','YesNo','Question')
    if ($answer -ne 'Yes') { exit 0 }
}
try { $appLock = [IO.File]::Open((Join-Path $dataRoot 'application.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
catch { throw 'mcmere Playを終了してから再試行してください。' }
try {
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
    Remove-Item -LiteralPath $appRoot -Recurse -Force
    [pscustomobject]@{ removed = $true; dataPreserved = $true; dataRoot = $dataRoot } | ConvertTo-Json
} finally { $appLock.Dispose() }
