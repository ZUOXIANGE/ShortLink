using System.Text.Json.Serialization;

namespace ShortLink.Contracts;

/// <summary>
/// 统一响应信封：HTTP 始终 200，业务成败只看 <see cref="Code"/>。
/// </summary>
/// <typeparam name="T">业务载荷类型。</typeparam>
public sealed record ApiResponse<T>(
    int Code,
    string Msg,
    T? Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static ApiResponse<T> Ok(T? data) => new(BizCodes.Success, "success", data);

    public static ApiResponse<T> Fail(
        int code,
        string msg,
        IReadOnlyDictionary<string, string[]>? errors = null)
        => new(code, msg, default, errors);
}

/// <summary>非泛型快捷入口，供异常处理器、中间件等无载荷场景使用。</summary>
public static class ApiResponse
{
    public static ApiResponse<object?> Ok() => new(BizCodes.Success, "success", null);

    /// <summary>带载荷的成功响应，省去显式写泛型参数。</summary>
    public static ApiResponse<T> Ok<T>(T data) => ApiResponse<T>.Ok(data);

    public static ApiResponse<object?> Fail(
        int code,
        string msg,
        IReadOnlyDictionary<string, string[]>? errors = null)
        => new(code, msg, null, errors);

    public static ApiResponse<T> Fail<T>(
        int code,
        string msg,
        IReadOnlyDictionary<string, string[]>? errors = null)
        => ApiResponse<T>.Fail(code, msg, errors);
}
