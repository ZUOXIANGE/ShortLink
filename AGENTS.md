# AGENTS.md · 项目约定

给协作者与 AI 代理的工程约定。改动前先读本文件与 `README.md`。

## 架构约束（已由 ArchUnitNET 守护）

依赖方向严格单向：

```
Api → Infrastructure → Application → Domain
                ↘ Contracts（Contracts 不引用任何服务端实现）
```

- `ShortLink.Domain`：实体与业务规则，**禁止**引用 EF Core、ASP.NET Core 或任何基础设施。
- `ShortLink.Application`：用例编排与端口接口（`Abstractions/`），**禁止**引用 EF Core 与 `Microsoft.AspNetCore.Http`；校验失败抛 `BusinessException(BizCodes.InvalidArgument, errors)`。
- `ShortLink.Infrastructure`：EF Core、仓储、JWT、缓存、后台服务；**禁止**引用 API。
- `ShortLink.Api`：端点、异常收口、认证/限流归一化；**禁止**引用 `ProblemDetails`（默认契约是统一信封）。
- `ShortLink.Contracts`：信封、业务码、DTO；前端与后端共享，**禁止**引用服务端层。

跑 `dotnet test ShortLink.slnx -c Debug` 时 `ShortLink.ArchitectureTests` 会校验以上规则。改动架构时，规则必须在同一次提交内更新。

## 契约规则

- 所有业务响应 `HTTP 200` + `{ code, msg, data }`；业务失败 `data = null`；字段级错误放 `errors`。
- 业务码只能新增，不得复用或改变既有含义；新增码必须同时更新 `BizCodes` 与 `docs/api/biz-codes.md`。
- 禁止在端点里手写 `try/catch` 返回不同结构；统一由 `GlobalExceptionHandler` 收口。
- 框架层结果（401/403/429/404/405）由 `AuthenticationSetup` 与 `ResponseNormalizationMiddleware` 归一化，新增中间件时确认不会绕过它们。
- 唯一例外：`/{code}` 跳转与 `/health`（含 `/health/ready`）保留真实 HTTP 语义，已在归一化白名单中排除。

## 编码约定

- 时间统一存 UTC（`DateTime`，列名后缀 `_utc`），DTO 也输出 UTC，由前端 `ToLocalTime()` 展示。
- **统计日界不走 UTC**：任何「按天分桶」或「今天」的概念（`click_date`、趋势区间、保留天数）一律取 `IStatisticsCalendar`，禁止 `DateTime.Today` / `DateOnly.FromDateTime(utcNow)` / `utcNow:yyyyMMdd`。写入侧把 `DateOnly` 当参数传进领域层（`ClickEvent.Create`），让 `click_date` 与查询用的是同一份日历；访客指纹的日期也必须同源，否则非 UTC 部署下独立访客会在 UTC 零点被劈成两段重复计数。
- 表名与列名使用 snake_case，在 `Persistence/Configurations` 中显式配置。
- 短码相关逻辑只在 `ShortCode`（Domain）里实现，端点与应用层不得自行拼装或校验字符集。
- 跳转路径上禁止新增同步数据库调用；需要额外数据时通过 `ILinkCache` 快照扩展。
- 后台任务不要在 `BackgroundService` 里解析 scoped 服务，必须 `CreateAsyncScope()`。
- 爬虫与来源信息解析集中在 `UserAgentParser` / `VisitorFingerprint`，不要在端点内散落实现。
- **来源地一律走 `IGeoLocationResolver`**，不要在端点里直接读 `X-Country` 一类请求头。采集处**禁止** `HtmlEncode`/`Sanitize`：条件路由拿入库值跟规则比对，编码一次就永远不命中，编码属于渲染职责。
- 启动期能验的配置错误就让它启动失败（时区 id、mmdb 文件是否存在、Kafka 地址），不要留到第一次请求——静默回落的症状（「配了库但省份还是空」）排查成本远高于起不来。
- 部署形态相关的事实要暴露成可读数据（如 `/health/info` 的 `cache.backplane`），不要只在文档里写「注意多实例」：文档不会进值班的视野，端点会。
- Git 钩子由 Husky.Net 驱动（`.husky/task-runner.json`）：`pre-commit` 只对暂存 `*.cs` 跑 `dotnet format`，不要往钩子里加全量 build / test；临时跳过用 `HUSKY=0 git commit`。

## 多租户与权限

- 短链、路由、转化、安全事件都带 `workspace_id`。**任何按 ID 直查的用例都必须再校验工作区**，否则换个 ID 就能读到别人的数据（`AnalyticsService.LoadVisibleAsync` / `ExportService.WriteClicksAsync` 是范例）。
- 可写判定统一走 `LinkPolicy.CanModify`（Domain 规则 + `ICurrentUser`），不要在端点或服务里各自判断角色。
- 端点权限用命名策略（`viewer` / `writer` / `manager` / `owner-only`），不要写裸 `RequireAuthorization()`。
- 角色判定口径只有一处：`UserRoles.Rank`（后端）与 `Permissions.Rank`（前端），两边必须一致。

## 条件路由与分流

- 路由选择必须是纯函数（`RouteSelector.Select`），不碰数据库、不持有状态，便于单测与缓存复用。
- 分流种子固定为 `SHA256(visitorKey|linkId|priority)`；改动公式等于推翻在跑的实验，需先在 ADR 里说明。
- 条件 JSON 解析失败按「该分支不命中」处理，绝不能让坏数据把整条短链打成 404。
- 跳转判定顺序不可随意调整（封顶在启停之前、密码在路由之前），理由见 `docs/adr/0005-routing-ab-and-attribution.md`。
- 安全事件写入必须包 try/catch：审计失败不能把一次正常跳转变成 500。

## 限流、探针与后台任务

- 新增限流策略必须**按来源 IP 分区**（`RateLimitPartition.Get($"{policy}:{ip}", …)`）。禁止直接 `AddFixedWindowLimiter("name")`——那是全进程共享窗口，一个人刷满就把所有人挡在外面。
- 分布式计数不能拆成 `INCR` + `EXPIRE` 两步：中间崩溃或主从切换丢 `EXPIRE` 会留下无 TTL 的键，那个 IP 永久被限流。必须用单条 Lua 脚本原子完成（见 `DistributedWindowRateLimiter`）。
- 依赖故障时的限流必须**回落本地窗口**，既不能放行（打挂 Redis 即绕过限流）也不能全拒；同时累加指标，否则配额悄悄失真没人知道。
- 存活探针（`/health`）不允许查外部依赖；数据库/缓存检查只放 `/health/ready`。把依赖加入存活探针 = 数据库抖动升级为全体重启。
- `/health/info` 是**诊断口不是探针**：输出缓存背板类型、跨实例失效能力、短链 TTL、统计时区。不要把编排系统的探针接到它上面，也不要往里塞需要认证的信息。
- `BackgroundService` 内必须 `CreateAsyncScope()` 取 scoped 服务；清理类任务要分批、设单次上限、批间停顿，并默认关闭（删除不可逆，必须由部署方显式开启）。
- 定时任务的可测性：把「执行一次」抽成 `public` 方法（如 `ClickRetentionService.PurgeAsync`），调度壳只负责触发，测试直接驱动方法而不改系统时间。

## 消息与异步副作用

- 副作用（点击落库、封顶审计）一律走消息总线，不要重新引入手写的 `Channel` 后台循环——两套异步机制等于两套失败语义与两套观测。
- **handler 默认禁止服务定位**（Wolverine 6 的 `ServiceLocationPolicy.NotAllowed`）。handler 需要的依赖要能内联构造；`DbContext` 这类由 `AddDbContext` 注册、构造参数是不透明工厂的对象，逐个用 `CodeGeneration.AlwaysUseServiceLocationFor<T>()` 放行，**不要改全局策略**——那等于对所有 handler 关掉这项检查。
- 批次里的写入要**显式提交/回滚**，且封顶判定必须放在累加之后（拿旧值判会漏停用）；通知类消息在提交**之后**发，回滚不留「已通知但没生效」的状态。
- 点击是可丢数据：批落库失败记日志 + 指标后**不重抛**，让总线对一批注定失败的写入无限重试只会堵住其他消息。
- 路由别只凭约定：local 模式走约定（消息类型 → 同名本地队列，与批处理执行队列一致）；只有 Kafka 模式才 `DisableConventionalLocalRouting()`，否则同一批点击会被本地与 broker 各消费一次。
- **高频消息不进本地消息表**：`Policies.UseDurableLocalQueues()` 是启动期端点策略，会把所有本地队列 enrol 进 inbox，且晚于 options 阶段的单队列 `BufferedInMemory()` 执行——写了也会被覆盖。要持久化就逐个 `LocalQueueFor<T>().UseDurableInbox()`，默认全部留在内存。
- **`No routes can be determined for Envelope ...` 是静默丢消息**：`PublishAsync` 照常返回、不抛不报错。新增会发布的消息类型后，务必跑一次覆盖该链路的集成测试。
- 验证异步副作用**不用固定 `Task.Delay`**，用 `WaitUntilAsync(condition, because)` 轮询：成功即继续、超时带原因失败。固定 sleep 会把启动期故障伪装成业务断言失败。

## MCP 工具层

- MCP 是**入口适配层**，与 HTTP 端点平级：只做协议转换 + 参数校验 + 调应用层。工具类**禁止**引用 `DbContext`、仓储或 `ShortLink.Infrastructure`，由 ArchUnitNET 守护。
- **工具失败必须 `IsError = true`**（经 `McpException`），不要把 `{code,msg,data}` 信封塞进工具返回值——模型会把失败当成功继续推理。HTTP 侧的信封与 MCP 侧的错误语义是两套协议，不能互相套用。
- 业务异常到工具错误的映射统一在 `McpToolRunner`；未预期异常只记日志并返回固定文案，错误文本会展示给模型，不能带堆栈或 SQL。
- 工具级鉴权必做：端点上的 `RequireAuthorization` 只保证「有令牌」，写工具要在方法里再过一次 `UserRoles.AtLeast`，对象级权限交给应用层 `LinkPolicy`。
- 工具返回值要投影：不要直接返回 `LinkDto`（30 字段）。只给「判断与下一步动作」需要的字段，列表类给 Top N，趋势点按需。
- 新增工具先看数量：单 Server 控制在 20–30 个以内，超出按领域拆分；工具描述必须写清「做什么 / 何时用 / 有无副作用」。
- `/mcp` 不进信封归一化，但**有两道关**：`ResponseNormalizationMiddleware` 白名单 + `JwtBearerEvents.OnChallenge/OnForbidden` 里判路径。401/403 是认证事件直接写的，绕过中间件；只改一处会漏。回归测试断言真实状态码，不要断言「客户端抛异常」。
- 高危工具标注 `Destructive = true` 并在描述里要求用户确认，不要指望模型自己谨慎。

## 前端约定

- `LinkDto` 等 DTO 里凡是「更新请求全量覆盖」的字段（如 `IpAllowList`），必须回传原文，否则编辑一次就会静默清空。
- Minimal API 的 `[AsParameters]` 会把**非可空属性当作必填参数**：分页/筛选类 DTO 的属性一律声明为可空，由 `EffectivePage` 等归一化，否则前端少传一个参数就是 40001。
- CSV 等二进制下载走 `FileSaver`（JS `Blob` + `download`），跨域下服务端要暴露 `Content-Disposition`。
- MudBlazor 的表单控件参数名以 9.x 为准（`MudTextField` 无 `Dense`、`MudSwitch` 无 `HelperText`、`MudTabs` 用 `TabPanelsClass`），新增页面前先看同目录既有写法。

## 本地编排（Aspire AppHost）

- 一条命令起全套：`dotnet run --project src/ShortLink.AppHost` → 仪表盘 15000、API 5080、Web 5081，MySQL/Redis 由 Docker 拉起。决策与代价见 `docs/adr/0010-aspire-apphost-orchestration.md`。
- **只有 `ShortLink.AppHost` 允许引用 `Aspire.Hosting.*`**。`Api` / `Infrastructure` / `Web` 禁止引 Aspire 客户端包与服务发现：应用侧只认 `IConfiguration`，否则同一份产物换到 compose / k8s 就跑不起来，线上故障也没法脱离 Aspire 复现。
- AppHost 只做「资源 → 显式配置键」的翻译（`Database__Provider` / `Cache__RedisConnectionString` / `Messaging__Transport`），不做隐式注入。
- **不引入 ServiceDefaults**：Api 自己已注册 OTel 三信号与 `/health`（含「存活探针不查依赖」的语义），再叠一层会重复上报并覆盖探针语义。仪表盘只通过 `OpenTelemetry__Endpoint` 接进去。
- 对外端口固定 5080 / 5081（WASM 的 `ApiBaseUrl` 与 `Cors:AllowedOrigins` 是浏览器读的静态配置，随机端口会表现成「登录不上」）；**依赖容器与 Aspire 自身内部端口一律留空自动分配**，别写死——本机随便一个安全客户端就可能占掉你固定的端口。
- 连接串不要图省事直接吃 `AddMySql` 生成的：项目依赖 `AllowUserVariables` / `UseAffectedRows` / `CharSet=utf8mb4`。Redis 反过来必须用资源自带的 `ConnectionStringExpression`：容器默认 `requirepass` 且口令随机，自己拼 `host:port` 的症状是只有 `/health/ready` 的 cache 一直 fail。
- 新增依赖（Kafka、PostgreSQL…）用 AppHost 配置开关暴露，默认值取「与生产同构」而不是「最省资源」，别让关键路径只在部署后第一次运行。
- 改动 AppHost 的注入后必须冒烟：`/health/ready` 返回 `ready`、建链后访问短码拿到 302、回查 `clickCount` 增长，才算这条链路真的通。

## 发布端（compose / Helm）

- 部署文件由 Aspire 发布端渲染，**不要手写第二套 compose / chart**：改 AppHost 就同时改了本地编排与部署产物。决策见 `docs/adr/0011-aspire-publish-compose-and-helm.md`。
- CLI 是本地工具（`.config/dotnet-tools.json` 的 `aspire.cli`），走 `dotnet tool restore`；不要依赖全局安装。
  - compose：`dotnet aspire publish -o artifacts/compose`
  - Helm：`ShortLink__PublishTarget=kubernetes dotnet aspire publish -o artifacts/k8s`
- **一次只能挂一个 compute environment**：模型里同时出现 `compose` 与 `k8s` 时管线会拒绝（`validate-compute-environments`）。目标靠配置 `ShortLink:PublishTarget` 选，不靠 `--publisher`——CLI 的 `aspire publish` 没有该参数。
- `publish` 只出文件不建镜像（`API_IMAGE` 为空是正常的），要镜像就再跑 `aspire do prepare-<env>` 或 `aspire deploy`。
- 发布模式下**任何密钥都不给默认值**：新增凭据类参数时用 `isPublishMode ? AddParameter(name, secret: true) : AddParameter(name, secret: true, value: 开发值)`，让产物里留下占位符。往 compose/Helm 里写 dev 口令是最容易被复制到生产的事故。
- 新增需要发布的服务：确认它是「容器可跑」的形态。Blazor WASM 这类静态站必须给 Dockerfile（见 `src/ShortLink.Web/Dockerfile`），别让 DevServer 混进生产镜像。
- 部署侧事实要显式写进产物：MySQL 健康检查与 `depends_on: service_healthy`、端口映射去重、仪表盘默认关闭，都在 `ConfigureComposeFile` 里做。

## 测试分层

- **默认路径只跑 SQLite 宿主**（`ApiFactory`），目标是秒级反馈；PR 与本地随手跑的都是这一层。
- **容器化用例由 `SHORTLINK_TEST_CONTAINERS=1` 门禁**（`ContainerTestGate`）。未开启时必须走 `Assert.Skip`，**不能静默通过**——静默通过等于永远不知道它没跑。
- 容器 fixture 必须自己同时持有「容器 + 宿主」：xunit 的 collection fixture 之间不能互相注入，拆成两个 fixture 就没有「容器先于宿主」的顺序保证。宿主要主动触发一次 `Factory.Services`，让迁移在集合初始化阶段跑完。
- 镜像版本与 AppHost / compose 产物保持一致（`mysql:8.4` / `redis:8.6`）；PostgreSQL 只用于测试（`postgres:17-alpine`，AppHost 不跑它）。只改一处等于让测试验的是另一个版本。
- 新增容器用例要**断言自己真的连到了目标形态**：MySQL 侧断言 `db.Database.IsMySql()`，PostgreSQL 侧断言 `db.Database.IsNpgsql()`，Redis 侧断言 `ICacheTopology.HasDistributedBackplane`。只看响应文案会被"配置看起来对、其实没连上"骗过。
- 三个 provider 走同一串动作（登录 → 建链 → 跳转 → 读统计），共用 `SmokeApi`；新增 provider 时照抄一个 fixture + 一个冒烟类，**不要复制一份 HTTP 辅助代码**。
- 别把限流额度抬到 1000 再断言「限流生效」：既有的 `ApiFactory` 抬额度是为了让业务用例不被 429 干扰，限流本身要用 `DistributedRateLimiterTests` 这类直接对 Redis 的用例验。

## 常用命令

```bash
dotnet build ShortLink.slnx
dotnet test ShortLink.slnx -c Debug
dotnet tool restore && dotnet husky install    # 安装 Git 钩子（pre-commit 格式化暂存文件）
dotnet run --project src/ShortLink.AppHost  # 15000 仪表盘 / 5080 API / 5081 Web，含 MySQL + Redis
dotnet aspire publish -o artifacts/compose # 产出 docker-compose.yaml + .env（需先 dotnet tool restore）
dotnet run --project src/ShortLink.Api      # 5080
dotnet run --project src/ShortLink.Web      # 5081
# 容器化集成测试（MySQL 方言 / Redis 原子限流，需要 Docker）
SHORTLINK_TEST_CONTAINERS=1 dotnet test tests/ShortLink.IntegrationTests -c Debug
dotnet ef migrations add <Name> --project src/ShortLink.Infrastructure.Migrations.MySql --startup-project src/ShortLink.Infrastructure.Migrations.MySql --output-dir Migrations
```

## 包管理

- 集中包版本管理（CPM），版本只在 `Directory.Packages.props` 声明，项目里不写 `Version`。
- 测试框架固定 xunit.v3 + Microsoft.Testing.Platform（`.NET 10` 不再支持 VSTest 入口，见 `global.json`）。
- MySQL 驱动使用 `Microting.EntityFrameworkCore.MySql`（MIT，Pomelo 分支），不要换回官方或 Pomelo 9.x（与 EF 10 不兼容），理由见 `docs/adr/0004-multi-database-provider.md`。
