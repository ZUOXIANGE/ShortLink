namespace ShortLink.Contracts;

/// <summary>
/// 业务码 → 前端提示文案的兜底映射。前端在 <c>msg</c> 为空时使用，避免出现空白提示。
/// </summary>
public static class ErrorMessages
{
    public static string From(int code, string? msg)
        => string.IsNullOrWhiteSpace(msg) ? BizCodes.Describe(code) : msg;
}
