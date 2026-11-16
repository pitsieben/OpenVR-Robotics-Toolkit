using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ViveTcpRecorder;

/// <summary>
/// Stores the GitHub token encrypted with Windows DPAPI (CurrentUser scope): only the same Windows
/// user on the same PC can decrypt token.dat. Copying the file to another machine or account is useless.
/// </summary>
public static class TokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ViveTcpRecorder.PlaneExchange.v1");

    /// <summary>Reads dir\token.dat; if only dir\token.txt exists, encrypts it to token.dat and deletes the plain file.</summary>
    public static string? Load(string dir)
    {
        var dat = Path.Combine(dir, "token.dat");
        var txt = Path.Combine(dir, "token.txt");
        try
        {
            if (File.Exists(txt))
            {
                var plain = File.ReadAllText(txt).Trim();
                if (plain.Length > 0)
                {
                    Save(dir, plain);
                    File.Delete(txt);
                    return plain;
                }
            }
            if (File.Exists(dat))
                return Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(dat))).Trim();
        }
        catch
        {
            // Unreadable (other user / other PC / no write access): fall through to the next location.
        }
        return null;
    }

    public static void Save(string dir, string token)
    {
        Directory.CreateDirectory(dir);
        var dat = Path.Combine(dir, "token.dat");
        if (File.Exists(dat)) File.SetAttributes(dat, FileAttributes.Normal); // hidden files can't be overwritten
        File.WriteAllBytes(dat, Protect(Encoding.UTF8.GetBytes(token.Trim())));
        File.SetAttributes(dat, File.GetAttributes(dat) | FileAttributes.Hidden);
    }

    // --- DPAPI via P/Invoke (avoids shipping an extra NuGet DLL next to the .gha) ---

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? desc, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr desc, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private const int UiForbidden = 0x1;

    public static byte[] Protect(byte[] data) => Run(data, true);
    public static byte[] Unprotect(byte[] data) => Run(data, false);

    private static byte[] Run(byte[] data, bool protect)
    {
        var hIn = GCHandle.Alloc(data, GCHandleType.Pinned);
        var hEnt = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var input = new DataBlob { Size = data.Length, Data = hIn.AddrOfPinnedObject() };
            var ent = new DataBlob { Size = Entropy.Length, Data = hEnt.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref input, "ViveExchange", ref ent, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            hIn.Free();
            hEnt.Free();
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }
}
