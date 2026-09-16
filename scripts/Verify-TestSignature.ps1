function Assert-TestSignature {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Thumbprint)
    if (-not ('McmerePlaySignatureCheck' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class McmerePlaySignatureCheck {
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct FileInfo { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File; public IntPtr Subject; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct TrustData {
        public uint Size; public IntPtr Policy; public IntPtr Sip; public uint UI; public uint Revocation;
        public uint Choice; public IntPtr File; public uint Action; public IntPtr State;
        public IntPtr URL; public uint Flags; public uint Context; public IntPtr Settings;
    }
    [DllImport("wintrust.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
    public static uint Verify(string path) {
        var file = new FileInfo { Size=(uint)Marshal.SizeOf(typeof(FileInfo)), Path=path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FileInfo)));
        Marshal.StructureToPtr(file, pointer, false);
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var data = new TrustData { Size=(uint)Marshal.SizeOf(typeof(TrustData)), UI=2, Choice=1, File=pointer, Action=1, Flags=0x1010 };
        try { return unchecked((uint)WinVerifyTrust(new IntPtr(-1), ref action, ref data)); }
        finally {
            data.Action=2; WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.DestroyStructure(pointer, typeof(FileInfo)); Marshal.FreeHGlobal(pointer);
        }
    }
}
'@
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.SignerCertificate.Thumbprint -ne $Thumbprint) { throw "Unexpected signing certificate: $Path" }
    $status = [McmerePlaySignatureCheck]::Verify([IO.Path]::GetFullPath($Path))
    # The test certificate is deliberately not installed into a trusted store by the build.
    if ($status -ne 0 -and $status -ne 0x800B0109L) { throw ('Signature verification failed: 0x{0:X8}: {1}' -f $status,$Path) }
}
