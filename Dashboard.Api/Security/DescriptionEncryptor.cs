using System.Security.Cryptography;
using System.Text;

namespace Dashboard.Api.Security;

public sealed class DescriptionEncryptor
{
    private readonly byte[] _key;

    public DescriptionEncryptor(string secret)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret.Trim()));
        _key = hash[..16];
    }

    public string? Protect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), encrypt: true));
    }

    public string? Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var plain = Transform(Convert.FromBase64String(value), encrypt: false);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] Transform(byte[] input, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var transformer = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transformer.TransformFinalBlock(input, 0, input.Length);
    }
}
