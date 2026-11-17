using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Options;

namespace ShortLink.Infrastructure.Security;

/// <summary>
/// API secret 的 AES-GCM 静态加密。格式为 <c>Base64(nonce | tag | ciphertext)</c>，
/// 每次加密使用新的 12 字节 nonce；主密钥来自 <c>PartnerApi:SecretEncryptionKey</c>。
/// </summary>
public sealed class AesGcmApiSecretProtector : IApiSecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[] _key;

    public AesGcmApiSecretProtector(IOptions<PartnerApiOptions> options)
    {
        var configured = options.Value.SecretEncryptionKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "PartnerApi:SecretEncryptionKey 不能为空；请注入 Base64 编码的 32 字节密钥");
        }

        _key = Convert.FromBase64String(configured);
        if (_key.Length != KeySize)
        {
            throw new InvalidOperationException(
                $"PartnerApi:SecretEncryptionKey 解码后必须是 {KeySize} 字节，当前为 {_key.Length} 字节");
        }
    }

    public string Protect(string secret)
    {
        var plaintext = Encoding.UTF8.GetBytes(secret);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var packed = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, NonceSize);
        ciphertext.CopyTo(packed, NonceSize + TagSize);

        return Convert.ToBase64String(packed);
    }

    public string Unprotect(string cipher)
    {
        var packed = Convert.FromBase64String(cipher);
        if (packed.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("凭据密文长度不合法");
        }

        var nonce = packed.AsSpan(0, NonceSize);
        var tag = packed.AsSpan(NonceSize, TagSize);
        var ciphertext = packed.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];

        using (var aes = new AesGcm(_key, TagSize))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return Encoding.UTF8.GetString(plaintext);
    }
}
