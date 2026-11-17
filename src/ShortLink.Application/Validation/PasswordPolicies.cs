namespace ShortLink.Application.Validation;

/// <summary>密码策略：8~128 位，至少包含一个字母与一个数字。</summary>
public static class PasswordPolicy
{
    public const int MinLength = 8;

    public const int MaxLength = 128;

    public static bool IsSatisfied(string? password)
        => !string.IsNullOrEmpty(password)
           && password.Length >= MinLength
           && password.Length <= MaxLength
           && password.Any(char.IsAsciiLetter)
           && password.Any(char.IsAsciiDigit);
}

/// <summary>新设置或更新的短链访问密码策略；既有短链验证密码时不套用该规则。</summary>
public static class LinkAccessPasswordPolicy
{
    public const int MinLength = 8;

    public const int MaxLength = 64;

    public static bool IsSatisfied(string? password)
        => !string.IsNullOrEmpty(password)
           && password.Length >= MinLength
           && password.Length <= MaxLength
           && password.Any(char.IsAsciiLetter)
           && password.Any(char.IsAsciiDigit);
}
