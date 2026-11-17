# 第三方开放 API

面向服务端到服务端的集成接口。认证使用 HMAC-SHA256 签名，不依赖用户密码或浏览器会话。

- 基础路径：`/api/v1/partner`
- 响应契约：HTTP 200 + `{ code, msg, data }`，业务成败只看 `code`（与后台 API 一致）
- 认证方案：`ApiKey`，与后台 JWT 完全隔离；JWT 不能调用 Partner 接口，API Key 也不能调用后台接口

## 1. 创建凭据

也可以在工作台「API 凭据」页面（仅所有者可见）完成创建、编辑、轮换与吊销；页面与下面的 HTTP 接口共用同一套校验。

由工作区所有者（owner）在后台调用：

```http
POST /api/v1/api-clients
Authorization: Bearer <JWT>
Content-Type: application/json

{
  "name": "订单系统",
  "userId": 12,
  "role": "member",
  "scopes": ["links:read", "links:write", "analytics:read"],
  "rateLimitPerMinute": 600,
  "allowedIps": "203.0.113.0/24, 198.51.100.10",
  "expiresAtUtc": null
}
```

响应中的 `secret` **只出现这一次**：

```json
{
  "code": 200,
  "msg": "success",
  "data": {
    "client": { "id": 1, "keyId": "ak_7f3c...", "secretPreview": "****9a2f", "...": "..." },
    "secret": "64 位十六进制字符串"
  }
}
```

`userId` 必须已经是当前工作区成员。`role` 是凭据的角色上限，不能高于该成员当前角色；
实际权限 = `min(成员当前角色, 凭据角色上限)`，再与 scope 取交集。

轮换与吊销：

- `POST /api/v1/api-clients/{id}/rotate`：生成新 secret，旧 secret 立即失效。
- `POST /api/v1/api-clients/{id}/revoke`：立即停用凭据。
- 停用绑定账号、移除工作区成员关系同样会让凭据在下一个请求失效。

## 2. 签名

### 2.1 请求头

| Header | 说明 |
|---|---|
| `X-Api-Key` | 凭据的 `keyId`，公开值 |
| `X-Timestamp` | Unix 秒级时间戳 |
| `X-Nonce` | 随机串，建议 16 字节以上（base64url / hex），窗口内不可重复 |
| `X-Signature` | `sha256=<hex>` |

时间戳允许 ±300 秒；nonce 在 600 秒内只能使用一次。

### 2.2 Canonical String

固定 6 行，行间用 `\n`（LF，不带 CR）：

```text
{METHOD}
{PATH}
{CANONICAL_QUERY}
{TIMESTAMP}
{NONCE}
{SHA256_HEX_OF_RAW_BODY}
```

规则：

1. `METHOD` 大写。
2. `PATH` 只取绝对路径，不含 Host 与 query，例如 `/api/v1/partner/links`。
3. `CANONICAL_QUERY`：
   - 按参数名的**序数（ordinal）升序**排列；
   - 每个 key / value 先做 URL 解码，再按 RFC3986 重新编码（空格是 `%20`，不是 `+`；十六进制大写）；
   - 用 `&` 连接 `key=value`；无 query 时该行为空。
4. `TIMESTAMP`、`NONCE` 原样放入。
5. 最后一行为**原始请求体的 SHA-256 十六进制小写**；无 body 时使用空串哈希
   `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`。

示例：

```text
POST
/api/v1/partner/links
domainId=1&status=enabled
1730000000
a1b2c3d4e5f6
720649efe0928cbf00374ce443ab41349443a989ef27a5a0c5743c5767f6d4d7
```

签名值 = `hex(HMAC-SHA256(secret, canonicalString))`，请求头写成 `sha256=<hex>`。

### 2.3 C# 参考实现

```csharp
static string Sign(string secret, HttpMethod method, string path,
    IEnumerable<KeyValuePair<string, string?>> query, byte[] body,
    string timestamp, string nonce)
{
    var canonicalQuery = string.Join('&', query
        .OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value ?? "")}"));

    var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
    var canonical = $"{method.Method.ToUpperInvariant()}\n{path}\n{canonicalQuery}\n{timestamp}\n{nonce}\n{bodyHash}";

    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}
```

发送时注意：签名用的 body 必须与 HTTP 实际发送的字节完全一致（不要先序列化一次签名、再序列化另一次发送）。

## 3. 端点

| Method | Path | Scope | 说明 |
|---|---|---|---|
| GET | `/partner/links` | `links:read` | 分页查询；支持 `keyword` / `domainId` / `status` / `sort` / `page` / `pageSize` |
| GET | `/partner/links/resolve?url=` | `links:read` | 按短链地址或短码反查 |
| GET | `/partner/links/{id}` | `links:read` | 详情 |
| POST | `/partner/links` | `links:write` | 创建，字段与后台 `CreateLinkRequest` 一致 |
| PUT | `/partner/links/{id}` | `links:write` | 更新，字段与后台 `UpdateLinkRequest` 一致 |
| POST | `/partner/links/{id}/enable` | `links:write` | 幂等启用 |
| POST | `/partner/links/{id}/disable` | `links:write` | 幂等停用 |
| DELETE | `/partner/links/{id}` | `links:delete` | 删除（不可逆，单独 scope） |
| GET | `/partner/links/{id}/stats` | `analytics:read` | 单链聚合统计（不含点击 IP 明细） |
| GET | `/partner/overview` | `analytics:read` | 工作区聚合总览 |

响应中的短链字段是 `PartnerLinkDto` 投影，不包含工作区、密钥状态、IP 名单等后台字段。

## 4. 数据权限

- 凭据绑定唯一工作区；**请求里没有也不能传 `workspaceId`**。
- 所有按 ID 的读写都走现有应用服务，工作区不匹配一律返回 `13001`（不泄露资源是否存在）。
- `Member` 角色只能修改自己创建的短链；`Admin` / `Owner` 可操作工作区内全部短链。
- scope 与角色是 AND：只读账号拿到 `links:write` scope 也不能写。
- 转化回调仍使用「每短链独立密钥」，不通过 API 凭据调用。
- 域名是全局资源，Partner v1 不提供域名列表；创建短链不传 `domainId` 时使用默认域名。

## 5. 错误码

| code | 含义 |
|---|---|
| 18001 | 凭据无效、停用或已过期 |
| 18002 | 签名校验失败 |
| 18003 | 时间戳超出窗口 |
| 18004 | nonce 已使用（疑似重放） |
| 18005 | 缺少所需 scope 或角色权限 |
| 18006 | 来源 IP 不在白名单 |
| 18007 | 请求体超过大小限制 |
| 18008 | 凭据不存在 |

完整码表见 [biz-codes.md](biz-codes.md)。

## 6. 运维注意

- 多实例部署必须配置 Redis：nonce 去重与限流配额都依赖它；未配置时启动日志会明确 Warning。
- `PartnerApi:SecretEncryptionKey` 必须通过环境变量注入（Base64 的 32 字节），缺失会启动失败。
- 凭据的 `last_used_at_utc` 按 5 分钟节流更新，不会为每个请求写库。
