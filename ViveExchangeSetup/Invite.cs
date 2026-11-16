using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ViveExchangeSetup;

/// <summary>What an invite carries. The token is only ever stored encrypted inside the link.</summary>
public sealed record InviteData(string Token, string Repo, string Branch, DateTime CreatedUtc);

/// <summary>
/// Invite link = viveexchange://invite/&lt;base64url(salt|nonce|tag|ciphertext)&gt;.
/// Encrypted with AES-256-GCM, key from the passphrase via PBKDF2-SHA256 (300k iterations).
/// Without the passphrase (sent over a different channel) the link is useless.
/// </summary>
public static class Invite
{
    public const string Scheme = "viveexchange";
    private const string Prefix = Scheme + "://invite/";
    private const int Iterations = 300_000;

    public static string Create(InviteData data, string passphrase)
    {
        if (passphrase.Length < 8) throw new ArgumentException("Passphrase must be at least 8 characters.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(data);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(Key(passphrase, salt), 16))
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(Scheme + "-v1"));
        CryptographicOperations.ZeroMemory(plain);
        return Prefix + B64Url(salt.Concat(nonce).Concat(tag).Concat(cipher).ToArray());
    }

    public static InviteData Open(string link, string passphrase)
    {
        var text = link.Trim();
        var i = text.IndexOf("invite/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0) text = text[(i + 7)..];
        text = text.TrimEnd('/');
        byte[] blob;
        try { blob = FromB64Url(text); } catch { throw new FormatException("This is not a valid invite link."); }
        if (blob.Length < 16 + 12 + 16 + 1) throw new FormatException("This invite link is incomplete.");

        var salt = blob[..16];
        var nonce = blob[16..28];
        var tag = blob[28..44];
        var cipher = blob[44..];
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(Key(passphrase, salt), 16);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.ASCII.GetBytes(Scheme + "-v1"));
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("Wrong passphrase, or the link was changed.");
        }
        var data = JsonSerializer.Deserialize<InviteData>(plain)!;
        CryptographicOperations.ZeroMemory(plain);
        return data;
    }

    private static byte[] Key(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
