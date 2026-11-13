namespace ShortLink.Contracts;

/// <summary>
/// 统一业务异常。业务代码任意层级可直接抛出以中断流程，由唯一的异常处理器转换成信封。
/// </summary>
public sealed class BusinessException : Exception
{
    public BusinessException(
        int code,
        string? message = null,
        IReadOnlyDictionary<string, string[]>? errors = null,
        Exception? innerException = null)
        : base(message ?? BizCodes.Describe(code), innerException)
    {
        Code = code;
        Errors = errors;
    }

    public int Code { get; }

    public IReadOnlyDictionary<string, string[]>? Errors { get; }

    public static BusinessException NotFound(int code) => new(code);

    public static BusinessException Invalid(int code, string message) => new(code, message);
}
