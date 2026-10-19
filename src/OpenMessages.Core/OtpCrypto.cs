using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace OpenMessages.Core;

/// <summary>Mirror of the phone's OtpCrypto; the format is specified in OpenMessages/docs/otp-sync.md.</summary>
public static class OtpCrypto
{
    public const int Version = 1;
    public const string CheckPlaintext = "openmessages-otp-sync-check";
    private const int TagBytes = 16;

    public sealed record Sealed(string Iv, string Ct);

    public static byte[] OtpAad(string uid) => Encoding.UTF8.GetBytes($"openmessages/otp/v{Version}/{uid}");
    public static byte[] CheckAad(string uid) => Encoding.UTF8.GetBytes($"openmessages/check/v{Version}/{uid}");

    /// <summary>The phone trims the passphrase before deriving, so we do too.</summary>
    public static string Normalize(string passphrase) => passphrase.Trim();

    public static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>AES-256-GCM; <c>Ct</c> is ciphertext followed by the 16-byte tag, as Java produces it.</summary>
    public static byte[] Decrypt(Sealed sealedBox, byte[] key, byte[] aad)
    {
        var iv = Convert.FromBase64String(sealedBox.Iv);
        var combined = Convert.FromBase64String(sealedBox.Ct);
        if (combined.Length < TagBytes) throw new CryptographicException("Ciphertext too short");
        var plaintext = new byte[combined.Length - TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Decrypt(iv, combined.AsSpan(0, plaintext.Length), combined.AsSpan(plaintext.Length), plaintext, aad);
        return plaintext;
    }

    public static Sealed Encrypt(byte[] plaintext, byte[] key, byte[] aad, byte[]? iv = null)
    {
        iv ??= RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length + TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(iv, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), aad);
        return new Sealed(Convert.ToBase64String(iv), Convert.ToBase64String(ciphertext));
    }

    /// <summary>True if <paramref name="key"/> opens the account's key-check record.</summary>
    public static bool Verify(byte[] key, Sealed check, string uid)
    {
        try
        {
            return Encoding.UTF8.GetString(Decrypt(check, key, CheckAad(uid))) == CheckPlaintext;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}

/// <summary>Decrypted contents of one synced code.</summary>
public sealed record OtpPayload(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("receivedAt")] long ReceivedAt);
