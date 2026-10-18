using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EqLink.Core;

public static class BundleCrypto
{
    private const int Iterations = 600_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static byte[] Encrypt(DeviceProfile profile, string passphrase)
    {
        profile.Validate();
        if (passphrase.Length < 12) throw new ArgumentException("Bundle passphrase must contain at least 12 characters.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(profile, Json);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes("EqLink-bundle-v1"));
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plaintext);
        return JsonSerializer.SerializeToUtf8Bytes(new EncryptedBundle(1, "PBKDF2-SHA256/AES-256-GCM", Iterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)), Json);
    }

    public static DeviceProfile Decrypt(ReadOnlySpan<byte> data, string passphrase)
    {
        var bundle = JsonSerializer.Deserialize<EncryptedBundle>(data, Json) ?? throw new InvalidDataException("Empty bundle.");
        if (bundle.Version != 1 || bundle.Algorithm != "PBKDF2-SHA256/AES-256-GCM" || bundle.Iterations != Iterations)
            throw new InvalidDataException("Unsupported bundle format.");
        var salt = Convert.FromBase64String(bundle.Salt);
        var nonce = Convert.FromBase64String(bundle.Nonce);
        var ciphertext = Convert.FromBase64String(bundle.Ciphertext);
        var tag = Convert.FromBase64String(bundle.Tag);
        if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16 || ciphertext.Length > 1_000_000)
            throw new InvalidDataException("Invalid bundle size.");
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes("EqLink-bundle-v1"));
            var profile = JsonSerializer.Deserialize<DeviceProfile>(plaintext, Json) ?? throw new InvalidDataException("Empty profile.");
            profile.Validate();
            return profile;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private sealed record EncryptedBundle(int Version, string Algorithm, int Iterations, string Salt, string Nonce, string Ciphertext, string Tag);
}
