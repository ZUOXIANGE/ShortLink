namespace ShortLink.Application.Options;

/// <summary>
/// 第三方开放接口的安全参数。
/// <para>
/// <see cref="SecretEncryptionKey"/> 是 secret 落库的 AES-GCM 主密钥：HMAC 验签需要拿到 secret 原文，
/// 所以不能只存哈希。该值必须外部注入，缺失或长度不足时<b>启动失败</b>——
/// 带一个空密钥上线，等于所有凭据密文都能被离线解开。
/// </para>
/// </summary>
public sealed class PartnerApiOptions
{
    public const string SectionName = "PartnerApi";

    /// <summary>AES-GCM 主密钥，Base64 编码的 32 字节。</summary>
    public string SecretEncryptionKey { get; set; } = string.Empty;

    /// <summary>签名时间戳允许的偏移（秒），默认 ±5 分钟。</summary>
    public int TimestampToleranceSeconds { get; set; } = 300;

    /// <summary>nonce 保留时长（秒），不能短于两倍时间戳窗口，否则窗口边界处可重放。</summary>
    public int NonceTtlSeconds { get; set; } = 600;

    /// <summary>请求体上限，默认 1 MB。</summary>
    public long MaxBodyBytes { get; set; } = 1_048_576;

    /// <summary>最后使用时间的写库节流窗口（秒）：同一次调用密集请求只落一次。</summary>
    public int LastUsedWriteIntervalSeconds { get; set; } = 300;

    /// <summary>启动期校验：配了开放接口就必须有一把合规主密钥。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SecretEncryptionKey))
        {
            throw new InvalidOperationException(
                "PartnerApi:SecretEncryptionKey 不能为空；请通过 PartnerApi__SecretEncryptionKey 注入 Base64 编码的 32 字节密钥");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(SecretEncryptionKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "PartnerApi:SecretEncryptionKey 必须是 Base64 字符串（32 字节密钥）", exception);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                $"PartnerApi:SecretEncryptionKey 解码后必须是 32 字节，当前为 {key.Length} 字节");
        }

        if (TimestampToleranceSeconds <= 0)
        {
            throw new InvalidOperationException("PartnerApi:TimestampToleranceSeconds 必须大于 0");
        }

        if (NonceTtlSeconds < TimestampToleranceSeconds * 2)
        {
            throw new InvalidOperationException(
                "PartnerApi:NonceTtlSeconds 必须不小于 TimestampToleranceSeconds 的两倍，否则时间窗边界处仍可重放");
        }

        if (MaxBodyBytes <= 0)
        {
            throw new InvalidOperationException("PartnerApi:MaxBodyBytes 必须大于 0");
        }
    }
}
