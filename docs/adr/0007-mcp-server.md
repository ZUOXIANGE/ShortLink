# ADR 0007：以 MCP Server 把短链能力暴露给 AI 客户端

- 状态：已采纳
- 日期：2026-11-11
- 相关：`src/ShortLink.Mcp/`、`src/ShortLink.Api/Program.cs`、`src/ShortLink.Api/Infrastructure/RateLimitingSetup.cs`、`src/ShortLink.Api/Infrastructure/ResponseNormalizationMiddleware.cs`

## 背景

平台已有完整的 HTTP API 与管理后台，但「让 AI 客户端直接建短链、查统计」需要一层面向模型的适配：

1. 模型需要**知道有哪些能力、参数怎么填**——这要求工具描述与 JSON Schema，而不是 HTTP 路由文档。
2. 模型需要**从失败中自我纠正**——"短链不存在" 与 "没有权限" 必须是可读的错误，而不是一个 200 响应里的 `{"code":13001}`。
3. 调用方是**自动化程序**，会批量、突发地调用，必须能限流与审计。

这三条是 MCP（Model Context Protocol）解决的问题，不是「给 REST 加个 AI 入口」能顺带解决的。

## 决策

### 1. 独立程序集，与 HTTP 端点平级

新建 `ShortLink.Mcp`，依赖 `Contracts` + `Application`，**不引用** `Infrastructure` 与 `Api`。

MCP 是**又一个入口适配层**：只做协议转换、参数校验、异常映射，业务一律走应用层用例。如果把建链逻辑写进工具方法，就会出现第二份工作区隔离与权限判定——那是最容易漏的一条安全边界。

这条约束由 ArchUnitNET 固化（`Mcp_Should_Not_Depend_On_Persistence_Or_Host`、`Mcp_Tools_Should_Not_Touch_DbContext`），不是靠 review 记性。

### 2. 端点映射留在宿主，Mcp 只注册服务

`AddShortLinkMcp()` 只注册工具，`MapMcp()` 在 `Program.cs` 里调用。原因：用哪个授权策略、挂不挂限流是宿主决策，Mcp 程序集不应反向依赖 `AuthorizationSetup` 的策略常量。

启用开关 `Mcp:Enabled` **只在映射端点时读取**，不在服务注册时读取。服务注册发生在配置完全合并之前（容器 `Mcp__Enabled=true`、测试宿主的 `AddInMemoryCollection` 都可能晚于注册），在注册阶段读开关会让「显式开启」静默失效——表现为端点凭空消失，排查代价很高。

`Mcp:Enabled` 默认 **false**：升级到本版本不应自动多出一个可被 AI 调用的写入口。

### 3. 错误语义：`IsError = true`，不套信封

这是本项目最容易搞错的一条，因为默认 HTTP 契约是「永远 200 + `{code,msg,data}`」。

| 层次 | 载体 | 谁处理 |
| --- | --- | --- |
| 工具执行失败（业务码、权限不足、参数不合法） | `McpException` → `CallToolResult.IsError = true` | 模型看到后可自行纠正 |
| 协议错误（工具不存在、请求格式错） | JSON-RPC error | 客户端框架，模型通常看不到 |

若让业务失败返回 HTTP 风格的 `ApiResponse<T>`，`IsError` 会是 `false?`，模型会把 `{"code":13001}` 当成成功结果继续推理，最终给出「链接已创建」这类错误结论。

实现集中在 `McpToolRunner`：捕获 `BusinessException` → `throw new McpException($"[{ex.Code}] {ex.Message}")`；未预期异常只记日志并返回固定文案（错误文本会展示给用户与模型，不能带堆栈或 SQL）。

**刻意的偏离**：规范建议「参数非法用 `McpProtocolException`（JSON-RPC -32602）」，本实现统一走 `IsError`。理由是参数校验由应用层 FluentValidation 承担（抛 `40001`），拆成两套语义会出现「同一个空值有时是协议错、有时是业务错」；且 `[40001] targetUrl 不能为空` 对模型同样是可纠正的输入错误。代价是这类错误会占用一轮模型推理。

### 4. 端点保留真实 HTTP 语义，不进信封归一化

MCP 客户端靠 **401** 触发重新认证、靠 JSON-RPC 错误体判断调用成败。把 `/mcp` 归一化成 `200 + {code:40101}` 后，客户端会认为连接成功却解析不出响应，典型表现是「连上了但没有工具」。

**这里有两道关，只改一道不够**（实现时踩过一次）：

1. `ResponseNormalizationMiddleware` 的白名单加入 `Mcp:Path`——挡的是 404/405 与业务异常的归一化。
2. `JwtBearerEvents.OnChallenge` / `OnForbidden` 里判断请求路径——**401/403 是在认证事件里直接写信封的，根本不经过中间件白名单**。第一版只加了中间件白名单，匿名请求仍然拿到 `200 + {code:40101}`。

两处都从 `IOptions<McpOptions>` 读路径，改 `Mcp:Path` 不需要同步改代码。401 响应额外带 `WWW-Authenticate: Bearer`，供客户端做资源发现（将来接 OAuth 2.1 时正是靠这个头）。

回归测试钉的是**真实状态码**而不是「客户端是否抛异常」：后者在两种实现下都会通过（信封也解析不出 JSON-RPC），断言 401 + `WWW-Authenticate` 才能真正防回归。

### 5. 认证复用 JWT，工具级鉴权与端点同口径

端点 `RequireAuthorization(AuthorizationSetup.Viewer)` 保证「已登录」；工具内部再用 `UserRoles.AtLeast` 判角色（读工具 `Viewer`，写工具 `Member`）。对象级判断交给应用层 `LinkPolicy`——MCP 不重复实现。

**已知差距**：MCP 规范要求 HTTP 传输按 OAuth 2.1 resource server 处理（含 Protected Resource Metadata 端点、动态客户端注册）。当前只支持静态 Bearer JWT，接入方需手工把登录得到的令牌配进客户端 header。这对自托管单租户场景够用，但对接第三方 AI 平台时是缺口，已记入技术债。

### 6. 工具设计：9 个，单一职责

| 工具 | 角色 | 读写 |
| --- | --- | --- |
| `shortlink_create` | Member | 写 |
| `shortlink_get` | Viewer | 读 |
| `shortlink_list` | Viewer | 读 |
| `shortlink_set_enabled` | Member | 写（幂等） |
| `shortlink_delete` | Member | 写（`Destructive = true`） |
| `shortlink_stats` | Viewer | 读 |
| `shortlink_dashboard` | Viewer | 读 |
| `shortlink_recent_clicks` | Viewer | 读 |
| `shortlink_list_domains` | Viewer | 读 |

不提供 `shortlink_update`：更新是全量覆盖（`UpdateLinkRequest` 的 `Password`/`IpAllowList`/路由都必须回传原文），让模型拼一个完整请求极易静默清空配置。启停通过 `ToggleAsync` 复用领域规则，不做「先读后写」。

返回值投影成 `LinkSummary` / `LinkStats` 等精简 record，不复用 `LinkDto`（30 个字段）。趋势点默认不返回（90 天会占掉大量上下文），维度只回 Top N。点击明细不返回原始 IP。

### 7. 限流按调用者分区

`/mcp` 用独立策略 `mcp`，分区键为 `sub`（无令牌时回落 IP）。AI 客户端常从同一出口 IP、同一账号批量调用：按 IP 分区会把一个团队算成一个人，按 `sub` 才对应真实配额归属。

阈值 `Mcp:PermitPerMinute` 默认 120。读写未分开配额——当前写工具耗时可忽略，分开会引入第二套配置而收益有限；调用量上来后再拆。

### 8. 可观测性独立于 HTTP 指标

Streamable HTTP 传输下每次工具调用都是 `POST /mcp`，HTTP 层永远 200，看状态码分不出工具成败。因此新增 `ShortLink.Mcp` 的 `ActivitySource` / `Meter`：

- `mcp.tool.name` / `mcp.tool.is_error` / `mcp.actor` / `mcp.biz_code`（span 标签）
- `shortlink.mcp.tool_calls`（`tool` / `is_error`）、`shortlink.mcp.tool_duration`、`shortlink.mcp.tool_rejections`

审计日志记工具名、用户、耗时、业务码，Serilog 结构化输出。

## 后果

- 增加一个入口程序集与 9 个工具，工具数远低于「单 Server ≤ 30」的经验阈值，暂不需要按领域拆分。
- 默认关闭，运维需显式开启并注入 `Mcp:Enabled=true`；关闭时 `/mcp` 无路由（不是 404 信封）。
- 测试用真实 `McpClient` 打通（8 条），覆盖工具发现、端到端读写、业务失败 `IsError`、只读账号越权、参数校验、匿名拒绝。协议层的行为差异只有真跑才知道，mock 会漏掉。
- 未实现：OAuth 2.1 动态客户端注册与 PRM、工具级读写分离配额、长任务（当前所有工具都在秒级返回，不需要 Tasks 扩展）。
- 依赖：`ModelContextProtocol.AspNetCore` **2.4.0**（2026-11-11 通过 nuget.org flat container 核实）。2.x 与 1.x 在 `Stateless` 默认值、Roots/Sampling API、Tasks 归属上均有断代差异，不要混用 1.x 示例。
