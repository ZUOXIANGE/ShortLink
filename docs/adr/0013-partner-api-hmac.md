# ADR 0013：第三方开放 API 的 HMAC 签名与数据权限

- 状态：已采纳
- 日期：2026-11-16
- 相关：`src/ShortLink.Domain/ApiClients/ApiClient.cs`、`src/ShortLink.Infrastructure/Security/ApiClientAuthenticator.cs`、`src/ShortLink.Api/Infrastructure/ApiKeyAuthenticationHandler.cs`、`src/ShortLink.Api/Endpoints/PartnerEndpoints.cs`、`docs/api/partner-api.md`

## 背景

需要让第三方系统（订单、客服、投放平台）在不共享用户密码的前提下创建和查询短链。可选方案：

| 方案 | 问题 |
| --- | --- |
| 复用 JWT | 令牌短、需要账号密码或刷新机制；第三方拿到的是「某个人」的完整后台身份 |
| 静态 API Key（请求头直传 secret） | 实现简单，但 secret 每次明文随请求传输，且无法防重放 |
| OAuth2 client credentials | 需要授权服务器、令牌刷新与吊销链路，对当前单体部署过重 |
| HMAC 请求签名 | 与现有转化回调一致，无状态、可防重放，第三方只需实现一段签名逻辑 |

选择 HMAC-SHA256。

## 决策

### 1. 凭据绑定工作区成员账号，而不是独立权限体系

`api_clients` 保存 `workspace_id` 与 `user_id`，认证成功后生成与 JWT 同构的 Claims：

```text
sub / name / role / ws / scope / client_id
```

因此 `WorkspaceContext`、`LinkService`、`LinkPolicy`、`AnalyticsService` 不需要任何改动，
工作区隔离与「成员只能改自己创建的短链」等规则自动生效。若为 API 单独造一套权限判断，
两条链路迟早会出现口径差异。

### 2. 有效角色 = min(成员当前角色, 凭据角色上限)

管理员账号也可以签发一个人为收窄的凭据（例如只允许 Member 行为），
避免「为了集成方便，直接发一把管理员密钥」。成员被降级或移除时，凭据立即跟着失效。

### 3. scope 与角色是 AND 关系

`links:write` 只说明「这个凭据被允许执行写操作」，不代表它能突破角色限制。
只读账号拿到写 scope 仍然会被授权策略拒绝。

### 4. secret 必须可逆存储

HMAC 验签需要 secret 原文，不能像密码一样只存哈希。使用 AES-GCM 加密落库，
主密钥来自 `PartnerApi:SecretEncryptionKey`，启动期校验长度；明文 secret 只在创建 / 轮换响应中出现一次。
若未来合规要求服务端不可还原密钥，应切换为 ECDSA P-256（客户端持私钥、服务端存公钥），
但这会改变第三方接入方式，需要新开 ADR。

### 5. 认证与限流的顺序

第三方签名认证是授权策略的一部分。若按常规把 `UseRateLimiter` 放在 `UseAuthorization` 之后，
签名失败的请求会先被授权短路，永远碰不到配额——攻击者可以用随机 keyId 无限打数据库和日志。

因此：

```text
UseAuthentication
  → PartnerAuthenticationMiddleware（只对 /api/*/partner 提前验签并写回 User）
  → UseRateLimiter
  → UseAuthorization
```

失败请求没有 `client_id`，限流按来源 IP 分区；成功请求按凭据配额分区。

### 6. 管理面收紧到 owner + JWT

签发凭据等于把工作区数据交给外部系统，因此 `/api/v1/api-clients` 只允许所有者通过 JWT 调用；
Partner 端点则显式绑定 `ApiKey` 方案，两套身份不能互相调用。

## 后果

- 多实例部署必须配置 Redis：nonce 去重依赖它；未配置时启动日志会 Warning（与缓存背板同口径）。
- 新增凭据表需要三套迁移（MySQL / PostgreSQL / SQLite），按既有方式维护。
- 外部契约以 `PartnerLinkDto` 为边界，后台 `LinkDto` 的字段演进不会直接冲击集成方。
- 域名是全局资源，Partner v1 不提供域名列表；点击明细含 IP，v1 只开放聚合统计。
