using ShortLink.Domain.ApiClients;

namespace ShortLink.Application.Abstractions.Persistence;

/// <summary>第三方 API 凭据仓储。</summary>
public interface IApiClientRepository
{
    /// <summary>认证路径使用：按公开 keyId 读取，不跟踪。</summary>
    Task<ApiClient?> FindByKeyIdAsync(string keyId, CancellationToken cancellationToken);

    /// <summary>管理路径使用：工作区隔离 + 跟踪态，便于直接修改。</summary>
    Task<ApiClient?> FindByIdAsync(long workspaceId, long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApiClient>> ListAsync(long workspaceId, CancellationToken cancellationToken);

    void Add(ApiClient client);

    /// <summary>节流更新最后使用时间：只有超过阈值才写库，避免每请求一次 UPDATE。</summary>
    Task TouchLastUsedAsync(
        long id,
        DateTime nowUtc,
        DateTime thresholdUtc,
        CancellationToken cancellationToken);
}
