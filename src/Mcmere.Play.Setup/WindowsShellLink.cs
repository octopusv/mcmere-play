using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace Mcmere.Play.Setup;

[SupportedOSPlatform("windows")]
public static class WindowsShellLink
{
    private static object Create() => Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!)!;
    public static void Write(string file, string target, string directory)
    {
        var instance = Create();
        try
        {
            var link = (IShellLinkW)instance;
            link.SetPath(target); link.SetWorkingDirectory(directory); link.SetIconLocation(target, 0);
            ((IPersistFile)instance).Save(file, true);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
    public static string Target(string file)
    {
        var instance = Create();
        try
        {
            ((IPersistFile)instance).Load(file, 0);
            var target = new StringBuilder(32768);
            ((IShellLinkW)instance).GetPath(target, target.Capacity, IntPtr.Zero, 4);
            return target.ToString();
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
