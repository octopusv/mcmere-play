using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mcmere.Distribution.Contracts;

namespace Mcmere.Play.Core;

public sealed record ActivityState(bool PrismRunning, bool GameRunning, bool Uncertain, string? Detail = null);
public interface IPlayActivity : IInstanceActivity
{
    ActivityState Read();
}
public sealed class ProcessActivity(PlayPaths paths) : IPlayActivity
{
    public ActivityState Read()
    {
        if (OperatingSystem.IsWindows()) return ReadWindows();
        var prism = false; var game = false; var uncertain = false;
        foreach (var name in new[] { "prismlauncher", "java", "javaw" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (RuntimeManager.IsProbe(process.Id)) continue;
                    try
                    {
                        var executable = ImagePath(process);
                        if (executable is null) { if (!process.HasExited) uncertain = true; continue; }
                        var owned = Path.GetFullPath(executable).StartsWith(paths.Runtimes.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                        if (!owned) continue;
                        if (name == "prismlauncher") prism = true; else game = true;
                    }
                    catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
                    { try { if (!process.HasExited) uncertain = true; } catch (InvalidOperationException) { } }
                }
            }
        }
        return new(prism, game, uncertain);
    }
    private ActivityState ReadWindows()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var ownSession)) return new(false, false, true, "session:" + Marshal.GetLastWin32Error());
        uint level = 0;
        if (WTSEnumerateProcessesEx(IntPtr.Zero, ref level, ownSession, out var entries, out var count))
        {
            var prismFound = false; var gameFound = false; var unknown = false;
            try
            {
                var bytes = Marshal.SizeOf<WtsProcess>();
                for (uint i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<WtsProcess>(IntPtr.Add(entries, checked((int)i * bytes)));
                    if (item.SessionId != ownSession || RuntimeManager.IsProbe((int)item.ProcessId)) continue;
                    var name = Marshal.PtrToStringUni(item.Name)?.ToLowerInvariant();
                    if (name is not ("java.exe" or "javaw.exe" or "prismlauncher.exe")) continue;
                    var process = OpenProcess(0x1000, false, (int)item.ProcessId);
                    if (process == IntPtr.Zero) { if (Marshal.GetLastWin32Error() != 87) unknown = true; continue; }
                    try
                    {
                        var length = 32768; var image = new StringBuilder(length);
                        if (!QueryFullProcessImageName(process, 0, image, ref length)) { unknown = true; continue; }
                        if (!Path.GetFullPath(image.ToString()).StartsWith(paths.Runtimes.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                        if (name == "prismlauncher.exe") prismFound = true; else gameFound = true;
                    }
                    finally { CloseHandle(process); }
                }
                return new(prismFound, gameFound, unknown, unknown ? "session_process_access" : null);
            }
            finally { WTSFreeMemoryEx(0, entries, count); }
        }
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return new(false, false, true, "snapshot:" + Marshal.GetLastWin32Error());
        var prism = false; var game = false; var uncertain = false;
        var failures = new List<string>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
            if (!Process32First(snapshot, ref entry)) return new(false, false, Marshal.GetLastWin32Error() != 18, "first:" + Marshal.GetLastWin32Error());
            do
            {
                var name = entry.Name.ToLowerInvariant();
                if (name is not ("java.exe" or "javaw.exe" or "prismlauncher.exe") || RuntimeManager.IsProbe((int)entry.ProcessId)) continue;
                if (!ProcessIdToSessionId(entry.ProcessId, out var session)) { if (Marshal.GetLastWin32Error() != 87) { uncertain = true; failures.Add("session:" + Marshal.GetLastWin32Error()); } continue; }
                if (session != ownSession) continue;
                var process = OpenProcess(0x1000, false, (int)entry.ProcessId);
                if (process == IntPtr.Zero) { var code = Marshal.GetLastWin32Error(); if (code != 87) { uncertain = true; failures.Add(name + ":" + code); } continue; }
                try
                {
                    var length = 32768; var image = new StringBuilder(length);
                    if (!QueryFullProcessImageName(process, 0, image, ref length)) { uncertain = true; failures.Add("image:" + Marshal.GetLastWin32Error()); continue; }
                    if (!Path.GetFullPath(image.ToString()).StartsWith(paths.Runtimes.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetFileName(image.ToString()).Equals("prismlauncher.exe", StringComparison.OrdinalIgnoreCase)) prism = true; else game = true;
                }
                finally { CloseHandle(process); }
            } while (Process32Next(snapshot, ref entry));
            return new(prism, game, uncertain, failures.Count > 0 ? string.Join(",", failures.Distinct()) : null);
        }
        finally { CloseHandle(snapshot); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeap;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WtsProcess
    {
        public uint SessionId;
        public uint ProcessId;
        public IntPtr Name;
        public IntPtr Sid;
    }
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateProcessesExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateProcessesEx(IntPtr server, ref uint level, uint sessionId, out IntPtr processes, out uint count);
    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemoryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSFreeMemoryEx(int type, IntPtr memory, uint count);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    public Task RequireIdleAsync(string instanceId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var state = Read();
        if (state.Uncertain) throw new DistributionException("activity_unknown", "起動中のプログラムを確認できません。確認してから再試行してください。");
        if (state.GameRunning) throw new DistributionException("game_running", "Minecraftを終了してから更新してください。");
        if (state.PrismRunning) throw new DistributionException("prism_running", "Prismを閉じてから環境を更新してください。");
        return Task.CompletedTask;
    }
    private static string? ImagePath(Process process)
    {
        if (!OperatingSystem.IsWindows()) return process.MainModule?.FileName;
        var handle = OpenProcess(0x1000, false, process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var size = 32768; var text = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, text, ref size) ? text.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder filename, ref int size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
