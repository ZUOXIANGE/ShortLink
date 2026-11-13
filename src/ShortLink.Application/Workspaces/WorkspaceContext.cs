using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Contracts;
using ShortLink.Domain.Users;

namespace ShortLink.Application.Workspaces;

/// <summary>
/// 请求级工作区上下文。优先用令牌里的 <c>ws</c> 声明，缺失时回落到默认工作区，
/// 并对解析结果做请求内缓存，避免同一请求内重复查库。
/// </summary>
public sealed class WorkspaceContext(ICurrentUser currentUser, IWorkspaceRepository workspaces)
{
    private long? _workspaceId;
    private string? _workspaceName;

    public async Task<long> GetIdAsync(CancellationToken cancellationToken)
    {
        if (_workspaceId is { } cached)
        {
            return cached;
        }

        if (currentUser.WorkspaceId is { } fromToken)
        {
            _workspaceId = fromToken;
            return fromToken;
        }

        var workspace = await workspaces.FindDefaultAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new BusinessException(BizCodes.InternalError, "缺少默认工作区，请检查初始化是否完成");

        _workspaceId = workspace.Id;
        _workspaceName = workspace.Name;
        return workspace.Id;
    }

    public async Task<string> GetNameAsync(CancellationToken cancellationToken)
    {
        if (_workspaceName is { } cached)
        {
            return cached;
        }

        var id = await GetIdAsync(cancellationToken).ConfigureAwait(false);
        var workspace = await workspaces.FindByIdAsync(id, cancellationToken).ConfigureAwait(false);
        _workspaceName = workspace?.Name ?? "默认工作区";
        return _workspaceName;
    }

    /// <summary>当前操作者角色；未登录时按只读处理。</summary>
    public UserRole Role => currentUser.RoleValue;
}
