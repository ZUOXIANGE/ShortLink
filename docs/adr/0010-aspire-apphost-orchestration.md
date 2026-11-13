# ADR 0010：用 Aspire AppHost 编排本地依赖与服务

- 状态：已采纳
- 日期：2026-11-12
- 相关：`src/ShortLink.AppHost/`、`aspire.config.json`、`Directory.Packages.props`（Aspire 编排包段）、`src/ShortLink.Api/appsettings.json`、`src/ShortLink.Web/wwwroot/appsettings.json`

## 背景

在接入 Aspire 之前，本地起一套「能真正验证迁移与缓存行为」的环境要手工做四件事：

1. 装并启动 MySQL（或退而用 SQLite，但那就永远跑不到 MySQL 迁移脚本）；
2. 装并启动 Redis（不配就只能用进程内缓存，多实例失效的问题永远看不到）；
3. 两个终端分别 `dotnet run` API 与 Web，顺序错了前端会先起、登录直接失败；
4. 手工把连接串、端口、CORS 白名单对齐。

这四步的可选性带来的是**验证盲区**：日常开发走 SQLite + 无 Redis 的路径，MySQL 方言、`utf8mb4`、分布式限流 Lua 脚本、缓存背板这些只有在部署后才第一次运行。同时新人上手成本高，口头传承的「先起哪个、端口多少」迟早会歪。

需要的是：一条命令拉起「依赖 + 服务」，并且**不把 Aspire 变成运行时的硬依赖**——生产仍然是普通容器，不能因为开发用了 Aspire 就要求部署环境也装 Dashboard。

## 决策

### 1. 新增独立项目 `src/ShortLink.AppHost`，应用项目零 Aspire 依赖

AppHost 只做一件事：把资源（容器、项目）翻译成**显式的配置键**。

```csharp
api.WithEnvironment("Database__Provider", "MySql")
   .WithEnvironment("Database__ConnectionString", connectionString)
   .WithEnvironment("Cache__RedisConnectionString", redis.Resource.ConnectionStringExpression);
```

- `ShortLink.Api` / `ShortLink.Infrastructure` / `ShortLink.Web` **不引用任何 `Aspire.*` 包**，也不使用服务发现。
- 应用侧照旧只读 `IConfiguration`，所以同一份产物在 Aspire / `dotnet run` / compose / k8s 下行为一致，故障可以脱离 Aspire 复现。
- 这条约束的代价是放弃 Aspire 的服务发现与 `WithReference` 魔法（`ConnectionStrings__xxx` 自动注入）：本项目只有两个服务、一个数据库，显式键比服务发现更值得。

### 2. 不引入 ServiceDefaults 项目

`AddServiceDefaults()` 会再注册一套 OpenTelemetry 三信号与健康检查，与 `ShortLink.Api/Program.cs` 里已有的注册（含按业务码埋点、`/health` 不查依赖的存活语义）冲突，表现为指标重复上报、探针语义被覆盖。

因此只在 AppHost 里把仪表盘的 OTLP 端点递进去：

```csharp
api.WithEnvironment("OpenTelemetry__Endpoint", builder.Configuration["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"]);
```

端口由 `launchSettings.json` 的 `ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=http://localhost:19000` 固定；未设置时不猜端口，宁可没有链路追踪，也不要让 Api 每次导出都失败刷日志。

### 3. 对外端口写死（API 5080 / Web 5081），依赖容器端口随机

- **为什么写死**：管理后台是 Blazor WASM，`ApiBaseUrl` 是烧在 `wwwroot/appsettings.json` 里、由浏览器读取的静态配置，**读不到环境变量**。端口一随机，`Cors:AllowedOrigins` 与 `Seed:DefaultDomainHost` 会同时失配，症状是「登录页点了没反应」——很难联想到是端口问题。写死 5080/5081 后，现有 appsettings 一个字都不用改。
- **为什么容器端口随机**：宿主机上可能已经装了 MySQL / Redis。开发机上实测就撞上过第三方进程占用 ASPIRE 默认要用的 20000 端口，因此连 AppHost 自己的 resource service 端口也不固定，交给 Aspire 动态分配。

### 4. 默认 MySQL + Redis，Kafka 与 SQLite 走开关

| 开关 | 默认 | 说明 |
| --- | --- | --- |
| `ShortLink:Database` | `mysql` | `sqlite` 时不拉容器，退回零依赖路径（快速验证前端/接口用） |
| `ShortLink:MySqlImageTag` / `ShortLink:MySqlVersion` | `8.4` / `8.4.0` | 必须成对维护：`ServerVersion` 决定 EF 方言分支 |
| `ShortLink:UseRedis` | `true` | 关掉即退回进程内缓存与本地限流窗口 |
| `ShortLink:UseKafka` | `false` | 打开会额外拉 Kafka 容器，并把 `Messaging:Transport` 切成 `kafka` |

默认值刻意选「与生产同构」而不是「最省资源」：本地跑不到 MySQL 迁移与 Redis Lua 限流，等于把这两块的风险全部推到部署后。

### 5. MySQL 连接串自己拼，不用 `AddMySql` 生成的

项目依赖 `AllowUserVariables` / `UseAffectedRows` / `CharSet=utf8mb4`（批量更新与中文行为）。连接串按既有格式显式拼装，从容器端点取 host/port、从参数资源取口令：

```csharp
var connectionString = ReferenceExpression.Create(
    $"Server={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Database=shortlink;...");
```

Redis 反过来——用 `RedisResource.ConnectionStringExpression`，因为 Aspire 的 Redis 容器默认开启 `requirepass` 且口令随机生成，自己拼 `host:port` 会漏掉 `password=`，症状是启动一切正常、只有 `/health/ready` 的 `cache` 一直 fail。

## 验证方式

```bash
dotnet run --project src/ShortLink.AppHost
```

- 仪表盘 <http://localhost:15000>（带一次性登录链接，打印在控制台）
- API <http://localhost:5080/health>、<http://localhost:5080/health/ready>
- 管理后台 <http://localhost:5081>

冒烟路径（已实测通过）：登录 `admin` → 建短链 → 访问短码拿 302 → 回查 `clickCount=1`（证明 MySQL 迁移、种子、Redis 缓存、消息批落库整条链路都在跑）。

## 后果

**收益**

- 一条命令替代「装库 + 装缓存 + 两个终端」；MySQL 迁移与 Redis 路径回到日常开发视野内。
- 依赖版本（MySQL 8.4 / Redis）由代码固定，团队环境一致。
- 仪表盘直接看到 traces / metrics / logs 与资源状态，排障不必先找日志文件。

**代价**

- 需要 Docker 在本机可用；CI 里跑 AppHost 会拉镜像，因此**测试仍然走 Testcontainers 与 SQLite**，不把 AppHost 引入测试链路。
- AppHost 多一个 `Aspire.AppHost.Sdk` 版本要对齐（`NoWarn ASPIRE010` 是因为团队不用 `aspire run` CLI）。
- 5080 / 5081 被占用时启动失败——这是刻意的：静默换端口只会把问题变成「前端登录不上」。
- 数据落在 Docker 卷 `shortlink-mysql-data`，需要重置时 `docker volume rm shortlink-mysql-data`。
