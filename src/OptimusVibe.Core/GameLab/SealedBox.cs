using System.Text;
using Sodium;

namespace OptimusVibe.GameLab;

/// <summary>
/// libsodium sealed box (crypto_box_seal: X25519 + XSalsa20-Poly1305), the encryption GitHub requires for Actions secrets.
/// Uses Sodium.Core (MIT) with the libsodium native library (ISC) from the "libsodium" NuGet package, which ships win-x64 / win-arm64 /
/// win-x86 builds; they are bundled into the single-file exe through IncludeNativeLibrariesForSelfExtract.
/// </summary>
public static class SealedBox
{
    public const int PublicKeyBytes = 32;

    /// <summary>Encrypts <paramref name="plaintext"/> to the repo's base64 public key; returns base64 (the <c>encrypted_value</c> GitHub expects).</summary>
    public static string Encrypt(string plaintext, string base64PublicKey)
    {
        byte[] key;
        try { key = Convert.FromBase64String(base64PublicKey); }
        catch (FormatException) { throw new ArgumentException("The repository public key is not valid base64.", nameof(base64PublicKey)); }
        if (key.Length != PublicKeyBytes) throw new ArgumentException($"The repository public key must be {PublicKeyBytes} bytes.", nameof(base64PublicKey));
        var message = Encoding.UTF8.GetBytes(plaintext);
        try
        {
            return Convert.ToBase64String(SealedPublicKeyBox.Create(message, key));
        }
        finally
        {
            Array.Clear(message); // don't leave the secret around longer than needed
        }
    }

    /// <summary>Opens a sealed box (tests; the app never decrypts).</summary>
    public static string Decrypt(string base64Cipher, byte[] publicKey, byte[] privateKey) =>
        Encoding.UTF8.GetString(SealedPublicKeyBox.Open(Convert.FromBase64String(base64Cipher), privateKey, publicKey));

    /// <summary>A fresh X25519 key pair (tests).</summary>
    public static (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
    {
        var pair = PublicKeyBox.GenerateKeyPair();
        return (pair.PublicKey, pair.PrivateKey);
    }
}
