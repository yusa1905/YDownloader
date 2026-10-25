using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace YDownloader.Debrid;

/// <summary>
/// Schützt Geheimnisse (den API-Key) mit der Windows-Datenschutz-API (DPAPI, Benutzerkonto): In <c>settings.json</c> steht nur
/// Chiffre, kein Klartext. Ein anderer Windows-Benutzer oder ein anderer Rechner kann sie nicht lesen, der Key muss dort neu
/// eingetragen werden. Per P/Invoke, weil dafür kein NuGet-Paket dazukommen soll.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("YDownloader.Debrid.v1");

    /// <summary>Verschlüsselt und liefert Base64.</summary>
    public static string Protect(string secret)
    {
        byte[] plain = Encoding.UTF8.GetBytes(secret);
        try
        {
            return Convert.ToBase64String(Run(plain, protect: true));
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    /// <summary>Entschlüsselt. Null, wenn es nicht geht (anderer Benutzer, anderer Rechner, beschädigt).</summary>
    public static string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;
        byte[]? plain = null;
        try
        {
            plain = Run(Convert.FromBase64String(protectedText), protect: false);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or Win32Exception)
        {
            return null;
        }
        finally
        {
            if (plain is not null) Array.Clear(plain);
        }
    }

    private static byte[] Run(byte[] input, bool protect)
    {
        var inBlob = ToBlob(input, out var inHandle);
        var entropy = ToBlob(Entropy, out var entropyHandle);
        var outBlob = new DataBlob();
        try
        {
            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, out outBlob)
                : CryptUnprotectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 0, out outBlob);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] result = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
            inHandle.Free();
            entropyHandle.Free();
        }
    }

    private static DataBlob ToBlob(byte[] bytes, out GCHandle handle)
    {
        handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        return new DataBlob { Size = bytes.Length, Data = handle.AddrOfPinnedObject() };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
