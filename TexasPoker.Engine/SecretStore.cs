using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TexasPoker.Engine;

/// <summary>
/// Keeps a secret (the dealer's sign-in key) encrypted in the config file with Windows' own data protection
/// (DPAPI), tied to this Windows account on this PC. A config folder zipped up and shared for help, or copied
/// to another PC, doesn't carry a working key: there it reads as signed out. Wine has the same protection.
/// Where it isn't there at all, the secret is kept as is, marked "plain:", so signing in still works.
/// </summary>
public static class SecretStore
{
    private const string ProtectedPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";
    private const int UiForbidden = 0x1;

    // Ties the encrypted key to this plugin: another program of the same Windows user can't open it by accident.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TexasPoker sign-in key v1");

    /// <summary>The secret as it's saved: encrypted, or empty for no secret.</summary>
    public static string Protect(string secret)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;
        try
        {
            return ProtectedPrefix + Convert.ToBase64String(Crypt(Encoding.UTF8.GetBytes(secret), protect: true));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or CryptographicException)
        {
            return PlainPrefix + secret;
        }
    }

    /// <summary>
    /// The secret from what <see cref="Protect"/> saved. Empty when there's none, or when it can't be opened here
    /// (another PC or Windows account): the dealer signs in again.
    /// </summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;
        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
            return stored[PlainPrefix.Length..];
        if (!stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return string.Empty;
        try
        {
            return Encoding.UTF8.GetString(Crypt(Convert.FromBase64String(stored[ProtectedPrefix.Length..]), protect: false));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var dataIn = new DataBlob { Size = input.Length, Data = inHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            DataBlob dataOut;
            var ok = protect
                ? CryptProtectData(ref dataIn, null, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out dataOut)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, out dataOut);
            if (!ok)
                throw new CryptographicException($"Windows data protection failed (error {Marshal.GetLastPInvokeError()}).");

            try
            {
                var output = new byte[dataOut.Size];
                Marshal.Copy(dataOut.Data, output, 0, dataOut.Size);
                return output;
            }
            finally
            {
                LocalFree(dataOut.Data);
            }
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
