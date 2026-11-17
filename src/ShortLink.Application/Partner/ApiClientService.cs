using FluentValidation;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Abstractions.Persistence;
using ShortLink.Application.Mapping;
using ShortLink.Application.Validation;
using ShortLink.Application.Workspaces;
using ShortLink.Contracts;
using ShortLink.Contracts.Partner;
using ShortLink.Domain.ApiClients;
using ShortLink.Domain.Users;
using ShortLink.Domain.Workspaces;

namespace ShortLink.Application.Partner;

/// <summary>
/// 第三方 API 凭据的生命周期管理。创建时需要校验绑定账号仍是工作区成员，
/// 且凭据角色上限不能高于该成员当前角色——否则等于绕过角色体系发了一把更高权限的密钥。
/// </summary>
public sealed class ApiClientService(
    IApiClientRepository clients,
    IUserRepository users,
    IWorkspaceRepository workspaces,
    ISecretGenerator secrets,
    IApiSecretProtector protector,
    ICurrentUser currentUser,
    WorkspaceContext workspace,
    IClock clock,
    IUnitOfWork unitOfWork,
    IValidator<CreateApiClientRequest> createValidator,
    IValidator<UpdateApiClientRequest> updateValidator)
{
    private const int MaxKeyAttempts = 5;

    public async Task<IReadOnlyList<ApiClientDto>> ListAsync(CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var list = await clients.ListAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (list.Count == 0)
        {
            return [];
        }

        var userIds = list.Select(client => client.UserId).Distinct().ToList();
        var names = (await users.ListAsync(userIds, cancellationToken).ConfigureAwait(false))
            .ToDictionary(user => user.Id, user => user.UserName);

        return list
            .OrderByDescending(client => client.Id)
            .Select(client => client.ToDto(names.GetValueOrDefault(client.UserId, string.Empty)))
            .ToList();
    }

    public async Task<CreateApiClientResponse> CreateAsync(
        CreateApiClientRequest request,
        CancellationToken cancellationToken)
    {
        await createValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var member = await LoadMemberAsync(workspaceId, request.UserId, cancellationToken).ConfigureAwait(false);
        var user = await users.FindByIdAsync(request.UserId, cancellationToken).ConfigureAwait(false)
                   ?? throw new BusinessException(BizCodes.UserNotFound);

        if (!user.IsEnabled)
        {
            throw new BusinessException(BizCodes.UserDisabled, "绑定账号已停用");
        }

        var role = ParseRole(request.Role);
        EnsureRoleWithinMember(role, member.Role);

        var scopes = ApiScopes.Normalize(request.Scopes);
        EnsureKnownScopes(request.Scopes);

        var (keyId, secret) = await CreateCredentialsAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;

        var client = ApiClient.Create(
            workspaceId,
            user.Id,
            request.Name,
            keyId,
            protector.Protect(secret),
            secret[^4..],
            role,
            scopes,
            request.RateLimitPerMinute,
            request.AllowedIps,
            NormalizeExpiry(request.ExpiresAtUtc),
            currentUser.UserId,
            now);

        clients.Add(client);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CreateApiClientResponse(client.ToDto(user.UserName), secret);
    }

    public async Task<ApiClientDto> UpdateAsync(
        long id,
        UpdateApiClientRequest request,
        CancellationToken cancellationToken)
    {
        await updateValidator.ValidateOrThrowAsync(request, cancellationToken).ConfigureAwait(false);

        var client = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (client.IsRevoked)
        {
            throw new BusinessException(BizCodes.Conflict, "凭据已吊销，无法修改");
        }

        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        var member = await workspaces
            .FindMemberAsync(workspaceId, client.UserId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new BusinessException(BizCodes.UserNotFound, "凭据绑定的账号已不是工作区成员");

        var role = string.IsNullOrWhiteSpace(request.Role) ? client.Role : ParseRole(request.Role);
        EnsureRoleWithinMember(role, member.Role);

        var scopes = request.Scopes is null ? client.Scopes : ApiScopes.Normalize(request.Scopes);
        if (request.Scopes is not null)
        {
            EnsureKnownScopes(request.Scopes);
        }

        client.Update(
            string.IsNullOrWhiteSpace(request.Name) ? client.Name : request.Name,
            role,
            scopes,
            request.RateLimitPerMinute ?? client.RateLimitPerMinute,
            request.AllowedIps is null ? client.AllowedIps : request.AllowedIps,
            request.ClearExpiry
                ? null
                : request.ExpiresAtUtc is null ? client.ExpiresAtUtc : NormalizeExpiry(request.ExpiresAtUtc),
            request.IsEnabled ?? client.IsEnabled);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var user = await users.FindByIdAsync(client.UserId, cancellationToken).ConfigureAwait(false);
        return client.ToDto(user?.UserName ?? string.Empty);
    }

    public async Task<RotateApiClientSecretResponse> RotateAsync(long id, CancellationToken cancellationToken)
    {
        var client = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (client.IsRevoked)
        {
            throw new BusinessException(BizCodes.Conflict, "凭据已吊销，无法轮换");
        }

        var secret = secrets.Create();
        client.RotateSecret(protector.Protect(secret), secret[^4..]);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var user = await users.FindByIdAsync(client.UserId, cancellationToken).ConfigureAwait(false);
        return new RotateApiClientSecretResponse(client.ToDto(user?.UserName ?? string.Empty), secret);
    }

    public async Task RevokeAsync(long id, CancellationToken cancellationToken)
    {
        var client = await LoadAsync(id, cancellationToken).ConfigureAwait(false);
        if (client.IsRevoked)
        {
            return;
        }

        client.Revoke(clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ApiClient> LoadAsync(long id, CancellationToken cancellationToken)
    {
        var workspaceId = await workspace.GetIdAsync(cancellationToken).ConfigureAwait(false);
        return await clients.FindByIdAsync(workspaceId, id, cancellationToken).ConfigureAwait(false)
               ?? throw new BusinessException(BizCodes.ApiClientNotFound);
    }

    private async Task<WorkspaceMember> LoadMemberAsync(
        long workspaceId,
        long userId,
        CancellationToken cancellationToken)
        => await workspaces.FindMemberAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false)
           ?? throw new BusinessException(BizCodes.UserNotFound, "绑定账号不是当前工作区成员");

    private async Task<(string KeyId, string Secret)> CreateCredentialsAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxKeyAttempts; attempt++)
        {
            var keyId = "ak_" + secrets.Create(12);
            if (await clients.FindByKeyIdAsync(keyId, cancellationToken).ConfigureAwait(false) is null)
            {
                return (keyId, secrets.Create());
            }
        }

        throw new BusinessException(BizCodes.Conflict, "凭据标识生成冲突，请重试");
    }

    private static UserRole ParseRole(string? value)
        => UserRoles.TryParse(value, out var role) && !string.IsNullOrWhiteSpace(value)
            ? role
            : throw new BusinessException(BizCodes.UserRoleInvalid);

    private static void EnsureRoleWithinMember(UserRole credentialRole, UserRole memberRole)
    {
        if (UserRoles.Rank(credentialRole) > UserRoles.Rank(memberRole))
        {
            throw new BusinessException(BizCodes.Forbidden, "凭据角色上限不能高于绑定账号的当前角色");
        }
    }

    private static void EnsureKnownScopes(IEnumerable<string> scopes)
    {
        var unknown = scopes.Where(scope => !ApiScopes.IsKnown(scope)).Distinct(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new BusinessException(
                BizCodes.InvalidArgument,
                "包含未知 scope",
                new Dictionary<string, string[]> { ["Scopes"] = [$"未知 scope：{string.Join(", ", unknown)}"] });
        }
    }

    private static DateTime? NormalizeExpiry(DateTime? value)
        => value?.ToUniversalTime();
}
