using System;
using System.Security.Cryptography;
using System.Text;

namespace TexasPoker.Engine;

/// <summary>
/// Locks a player's private table data (their hole cards) so only their browser can read it.
///
/// Each player's private link ends in <c>#token</c>. Browsers never send the part after <c>#</c> to a
/// server, so the token stays between the plugin and that player. From it:
/// <list type="bullet">
/// <item>the <b>lookup</b> (a hash) is what the page sends to the server to fetch its data, and what the
/// server files that data under; it can't be turned back into the token;</item>
/// <item>the <b>key</b> (HKDF-SHA256) encrypts the data with AES-256-GCM.</item>
/// </list>
/// The server, Cloudflare and their logs only ever see lookups and ciphertext. The web page does the same
/// derivation with WebCrypto to decrypt.
/// </summary>
public static class CardSealer
{
    private const string LookupPrefix = "texaspoker-lookup-v1:";
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("texaspoker-cards-v1");

    /// <summary>What the page asks the server for: base64url(SHA-256(prefix + table + ":" + token)), 22 chars.</summary>
    public static string Lookup(string tableId, string token)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{LookupPrefix}{tableId}:{token}"));
        return Base64Url(hash)[..22];
    }

    /// <summary>base64url(nonce ‖ ciphertext ‖ tag), with the lookup as associated data.</summary>
    public static string Seal(string tableId, string token, string plaintext)
    {
        var key = DeriveKey(tableId, token);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plaintext);
        var sealedBytes = new byte[12 + data.Length + 16];
        nonce.CopyTo(sealedBytes, 0);

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, sealedBytes.AsSpan(12, data.Length), sealedBytes.AsSpan(12 + data.Length, 16),
                    Encoding.UTF8.GetBytes(Lookup(tableId, token)));
        return Base64Url(sealedBytes);
    }

    /// <summary>The reverse of <see cref="Seal"/>; throws if the token is wrong or the data was changed.</summary>
    public static string Open(string tableId, string token, string sealedText)
    {
        var sealedBytes = FromBase64Url(sealedText);
        var plain = new byte[sealedBytes.Length - 28];
        using var aes = new AesGcm(DeriveKey(tableId, token), 16);
        aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(12, plain.Length), sealedBytes.AsSpan(sealedBytes.Length - 16),
                    plain, Encoding.UTF8.GetBytes(Lookup(tableId, token)));
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// The signature a player's page puts on an action it sends: base64url(HMAC-SHA256(key, payload)), the key
    /// derived from their token like the card key but for a different purpose. Only the plugin and that
    /// player's browser know the token, so the server (or anyone between) can't forge or alter an action.
    /// </summary>
    public static string SignAction(string tableId, string token, string payload)
    {
        using var hmac = new HMACSHA256(DeriveActionKey(tableId, token));
        return Base64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    public static bool VerifyAction(string tableId, string token, string payload, string signature)
    {
        var expected = Encoding.ASCII.GetBytes(SignAction(tableId, token, payload));
        var given = Encoding.ASCII.GetBytes(signature);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    private static byte[] DeriveKey(string tableId, string token) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(token), 32, Encoding.UTF8.GetBytes(tableId), KeyInfo);

    private static readonly byte[] ActionKeyInfo = Encoding.UTF8.GetBytes("texaspoker-actions-v1");

    private static byte[] DeriveActionKey(string tableId, string token) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(token), 32, Encoding.UTF8.GetBytes(tableId), ActionKeyInfo);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }
}
