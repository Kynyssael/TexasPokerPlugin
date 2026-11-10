using System;
using System.Security.Cryptography;
using System.Text;

namespace TexasPoker.Engine;

/// <summary>
/// Locks what the commentator sees (every player's cards and chances) so only the commentator's browser can read it.
///
/// The commentator's page makes its own key pair (ECDH P-256) the first time it opens, keeps the private key in that
/// browser, and gives the server only the public key. Each dealer's plugin seals every update to that public key: a new
/// key pair of its own each time, the shared secret through HKDF-SHA256 into an AES-256-GCM key. So the server, which
/// passes the updates on (after the delay), only ever holds ciphertext, even though several dealers' plugins write to
/// one commentator. The page does the same with WebCrypto to open them.
/// </summary>
public static class CommentatorSealer
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("eorzean-commentator-v1");

    /// <summary>base64url(ephemeral public key (65 bytes) ‖ nonce (12) ‖ ciphertext ‖ tag (16)).</summary>
    /// <param name="publicKey">The commentator browser's public key: base64url of the raw (uncompressed) P-256 point.</param>
    public static string Seal(string publicKey, string plaintext)
    {
        using var peer = ImportPublic(FromBase64Url(publicKey));
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralRaw = Raw(ephemeral.ExportParameters(false));
        var key = DeriveKey(ephemeral.DeriveRawSecretAgreement(peer.PublicKey), ephemeralRaw);

        var data = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var output = new byte[65 + 12 + data.Length + 16];
        ephemeralRaw.CopyTo(output, 0);
        nonce.CopyTo(output, 65);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, data, output.AsSpan(77, data.Length), output.AsSpan(77 + data.Length, 16), ephemeralRaw);
        return Base64Url(output);
    }

    /// <summary>The reverse of <see cref="Seal"/>, with the commentator's private key (tests).</summary>
    public static string Open(ECDiffieHellman commentator, string sealedText)
    {
        var bytes = FromBase64Url(sealedText);
        var ephemeralRaw = bytes.AsSpan(0, 65).ToArray();
        using var peer = ImportPublic(ephemeralRaw);
        var key = DeriveKey(commentator.DeriveRawSecretAgreement(peer.PublicKey), ephemeralRaw);
        var plain = new byte[bytes.Length - 93];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(bytes.AsSpan(65, 12), bytes.AsSpan(77, plain.Length), bytes.AsSpan(bytes.Length - 16), plain, ephemeralRaw);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>
    /// What the commentator link's token is filed under on the server: base64url(SHA-256(prefix + tournament + ":" +
    /// token)), 22 characters. The page works out the same from its link; the token itself never leaves the browser.
    /// </summary>
    public static string Lookup(string tournamentId, string token) =>
        Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes($"eorzean-commentator-lookup-v1:{tournamentId}:{token}")))[..22];

    /// <summary>A public key as the page sends it: base64url of the raw point.</summary>
    public static string PublicKeyOf(ECDiffieHellman key) => Base64Url(Raw(key.ExportParameters(false)));

    /// <summary>Whether text is a P-256 public key this can seal to.</summary>
    public static bool IsPublicKey(string text)
    {
        try
        {
            using var key = ImportPublic(FromBase64Url(text));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] DeriveKey(byte[] secret, byte[] ephemeralRaw) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, ephemeralRaw, Info);

    private static ECDiffieHellman ImportPublic(byte[] raw)
    {
        if (raw.Length != 65 || raw[0] != 4)
            throw new ArgumentException("Not a raw P-256 public key.");
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = raw.AsSpan(1, 32).ToArray(), Y = raw.AsSpan(33, 32).ToArray() },
        });
    }

    private static byte[] Raw(ECParameters p)
    {
        var raw = new byte[65];
        raw[0] = 4;
        p.Q.X!.CopyTo(raw, 1);
        p.Q.Y!.CopyTo(raw, 33);
        return raw;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - (padded.Length % 4)) % 4));
    }
}
