using FluentValidation;
using ShortLink.Contracts;

namespace ShortLink.Application.Validation;

/// <summary>把 FluentValidation 的校验结果转换成统一业务异常（<c>40001</c> + 字段级 errors）。</summary>
public static class ValidationGuard
{
    public static async Task ValidateOrThrowAsync<T>(
        this IValidator<T> validator,
        T instance,
        CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(instance, cancellationToken).ConfigureAwait(false);
        if (result.IsValid)
        {
            return;
        }

        var errors = result.Errors
            .GroupBy(e => string.IsNullOrEmpty(e.PropertyName) ? "_" : e.PropertyName)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).Distinct().ToArray(),
                StringComparer.Ordinal);

        throw new BusinessException(BizCodes.InvalidArgument, "参数校验失败", errors);
    }
}
