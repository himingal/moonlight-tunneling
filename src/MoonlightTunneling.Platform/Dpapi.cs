using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MoonlightTunneling.Platform;

/// <summary>Encrypts secrets (WireGuard private keys) to the current Windows user.</summary>
public static class Dpapi
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static string Protect(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(Transform(data, protect: true));
    }

    public static string Unprotect(string protectedBase64)
    {
        var data = Convert.FromBase64String(protectedBase64);
        return Encoding.UTF8.GetString(Transform(data, protect: false));
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new Native.DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            bool ok = protect
                ? Native.CryptProtectData(ref input, "MoonlightTunneling", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : Native.CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new Win32Exception();
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                Native.LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }
}
