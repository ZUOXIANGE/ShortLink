# ShortLink · 自建短链平台

[![CI](https://github.com/ZUOXIANGE/ShortLink/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/ZUOXIANGE/ShortLink/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/github/license/ZUOXIANGE/ShortLink)](https://github.com/ZUOXIANGE/ShortLink/blob/main/LICENSE.txt)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Blazor WebAssembly](https://img.shields.io/badge/Blazor-WASM-512BD4?logo=blazor&logoColor=white)](https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor)
[![EF Core](https://img.shields.io/badge/EF%20Core-10-512BD4?logo=dotnet&logoColor=white)](https://learn.microsoft.com/ef/core/)
[![Last commit](https://img.shields.io/github/last-commit/ZUOXIANGE/ShortLink)](https://github.com/ZUOXIANGE/ShortLink/commits/main)

一套可自托管的短链接平台，覆盖「建链 → 跳转 → 统计 → 归因」完整链路。前后端完全分离：后端只提供统一信封的 HTTP API，前端是独立部署的 Blazor WebAssembly 单页应用，通过 Bearer Token 调用，不共享服务端会话。

| 维度 | 选型 |
| --- | --- |
| 后端 | .NET 10 · ASP.NET Core Minimal API · EF Core 10 |
| 数据库 | MySQL 8（生产）· SQLite（零依赖启动）· PostgreSQL |
| 前端 | Blazor WebAssembly · MudBlazor 9 |
| 缓存 | FusionCache（防击穿 + fail-safe，可选 Redis 背板） |
| 消息 | Wolverine（进程内批处理，可切换 Kafka） |
| 认证 | JWT（HS256）+ PBKDF2-SHA256，角色 `owner` / `admin` / `member` / `viewer` |
| 可观测性 | OpenTelemetry traces / metrics / logs + Serilog |

## 设计基线

- **跳转路径不碰数据库**。一次跳转 = 一次缓存读取 + 一次消息发布；短链与条件路由规则随缓存快照一起下发，路由选择是纯内存计算。
- **统一契约**。业务响应一律 `HTTP 200 + { code, msg, data }`，401/403/429/404/405 由中间件归一化；`/{code}` 跳转与 `/health*` 是显式例外，保留真实状态码。
- **基础设施可替换**。数据库、缓存背板、消息传输、来源地解析全部通过端口接口切换，应用层不依赖具体实现。
- **分层由测试守护**。`Api → Infrastructure → Application → Domain` 单向依赖，ArchUnitNET 在 `dotnet test` 中校验。

## 核心能力

**短链与跳转**

- 创建 / 编辑 / 启停 / 删除，自定义短码（Base62、保留字保护），301/302/307/308，有效期、访问密码、备注
- 多域名：每域名独立协议与短码空间，Host 无法匹配时回落默认域名
- 三级跳转链路：域名缓存 → 短链缓存 → 回源；停用 / 过期 / 不存在返回带品牌的 HTML 落地页（真实 404/410）

**统计**

- 今日 / 累计点击、独立访客（IP + UA + 统计日哈希）、按天趋势、来源 / 设备 / 浏览器 / 系统 / 国家 TopN、最近访问明细
- 点击经消息总线批量落库（500 条或 500ms），计数增量更新，不阻塞跳转
- `Analytics:TimeZone` 决定分桶日界，写入与读取共用 `IStatisticsCalendar`

**运营**

- 工作区隔离与四级角色，全局域名仅默认工作区 `owner` 可写
- 批量启停 / 删除 / 改域名 / 改时间窗，单事务 + 逐条结果码，上限 500
- CSV 导出：短链列表与点击明细，流式写出、UTF-8 BOM、CSV 注入防护
- 浏览器端生成二维码并下载 PNG，短链内容不出设备
- 点击封顶与时间窗，达阈值自动停用并记安全事件

**路由与归因**

- 条件路由：国家 / 省份、设备、浏览器、系统、语言、Referer、查询参数；优先级 + 条件组（AND/OR）+ 兜底
- A/B 实验：同优先级内按权重分流，种子 `SHA256(访客指纹|短链|优先级)`，同一访客稳定命中同一分支
- 转化回调：HMAC-SHA256 签名，`(link_id, event_id)` 唯一索引幂等，按分支统计转化率与价值
- 安全策略：IP 黑白名单（CIDR）、机器人策略、目标地址扫描、安全事件台账、匿名滥用举报

**工程化**

- 限流按分区键隔离：登录 / 转化 / 举报按来源 IP，访问密码按 IP + 短码，MCP 按调用者；配置 Redis 后自动切换 Lua 原子计数，故障回落本地窗口并累加指标
- `/health` 只证明进程活着，`/health/ready` 校验数据库与缓存，`/health/info` 输出缓存拓扑与统计时区
- 点击明细保留策略：分批删除、数据库租约互斥、默认关闭
- MCP Server（`ShortLink.Mcp`，默认关闭）：AI 客户端可经 `/mcp` 建链、查数、看统计，失败返回 `IsError = true`

## 快速开始

### 一条命令（Aspire + Docker）

```bash
dotnet run --project src/ShortLink.AppHost
```

| 入口 | 地址 |
| --- | --- |
| Aspire 仪表盘（资源状态 / 容器日志） | <http://localhost:15000> |
| OpenObserve（traces / metrics / logs） | 端口随机，见仪表盘里的 `openobserve` 资源 |
| API | <http://localhost:5080> |
| 管理后台 | <http://localhost:5081> |

本地开发账号（**仅用于本机编排，生产必须全部替换**）：

| 用途 | 账号 | 密码 | 来源 |
| --- | --- | --- | --- |
| 管理后台 / API | `admin` | `Admin@123456` | AppHost 的 `seed-admin-password` 参数（`Seed__AdminPassword`） |
| OpenObserve（组织 `default`） | `root@shortlink.local` | `Observe@123456` | AppHost 的 `ZO_ROOT_USER_EMAIL` / `ZO_ROOT_USER_PASSWORD` |
| MySQL（容器） | `root` | `ChangeMe_123!` | AppHost 的 `mysql-password` 参数 |

OpenObserve 的 OTLP 入口是 `http://<host>:<port>/api/default`、HTTP Basic 认证；AppHost 会自动把端点、协议与 `Authorization` 头注入 API，无需手工配置。

默认用 Docker 拉起 MySQL 8.4 与 Redis，按依赖顺序启动 API 与 Web。零容器模式：

```bash
dotnet run --project src/ShortLink.AppHost -- --ShortLink:Database=sqlite --ShortLink:UseRedis=false
```

### 本地零依赖（SQLite）

```bash
dotnet build ShortLink.slnx
dotnet run --project src/ShortLink.Api    # http://localhost:5080
dotnet run --project src/ShortLink.Web    # http://localhost:5081
```

首次启动自动执行迁移，并创建管理员 `admin / Admin@123456`（来源 `Seed:AdminPassword`，**登录后立即修改**）与默认域名 `localhost:5080`。

### 生产配置（MySQL）

```jsonc
"Database": {
  "Provider": "MySql",
  "ConnectionString": "Server=127.0.0.1;Port=3306;Database=shortlink;User Id=shortlink;Password=***;CharSet=utf8mb4;AllowUserVariables=true;UseAffectedRows=false;",
  "MySqlVersion": "8.0.36"
}
```

常用环境变量：`Jwt__SigningKey`（≥32 字节）、`Seed__AdminPassword`、`Cache__RedisConnectionString`、`OpenTelemetry__Endpoint`、`OpenTelemetry__Headers`。API 启动时自动迁移，无需手工 `database update`。

接 OpenObserve 时：`OTEL_EXPORTER_OTLP_ENDPOINT=http://<host>:<port>/api/<org>`、`OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`、`OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic%20<base64(email:password)>`（环境变量形态下空格要写成 `%20`；走 `OpenTelemetry__Headers` 配置时用普通空格）。

### 测试

```bash
dotnet test ShortLink.slnx -c Debug
SHORTLINK_TEST_CONTAINERS=1 dotnet test tests/ShortLink.IntegrationTests -c Debug   # MySQL / PostgreSQL / Redis 方言
```

默认只跑 SQLite 宿主，秒级反馈；容器用例未开启时标记为跳过，而不是静默通过。

### 发布

```bash
dotnet tool restore
dotnet aspire publish -o artifacts/compose                          # docker-compose.yaml + .env
ShortLink__PublishTarget=kubernetes dotnet aspire publish -o artifacts/k8s
```

`publish` 只生成文件不构建镜像；参数与密钥均为占位符，需再执行 `dotnet aspire do prepare-shortlink` 或 `dotnet aspire deploy`。详见 `docs/adr/0011-aspire-publish-compose-and-helm.md`。

## API 概览

业务接口统一返回 `{ "code": 200, "msg": "success", "data": ... }`，失败时 `data` 为 `null`。完整码表见 `docs/api/biz-codes.md`。

| 域 | 端点 |
| --- | --- |
| 认证 | `POST /api/v1/auth/login` · `GET /api/v1/auth/me` · `POST /api/v1/auth/change-password` |
| 短链 | `GET/POST /api/v1/links` · `GET/PUT/DELETE /api/v1/links/{id}` · `POST /api/v1/links/{id}/toggle` · `POST /api/v1/links/batch` |
| 统计 | `GET /api/v1/links/{id}/stats` · `GET /api/v1/links/{id}/clicks` · `GET /api/v1/dashboard/overview` |
| 导出 | `GET /api/v1/links/export` · `GET /api/v1/links/{id}/clicks/export` |
| 路由 | `GET/PUT /api/v1/links/{id}/routes` · `POST /api/v1/links/{id}/routes/test` |
| 转化 | `GET /api/v1/links/{id}/conversion-key` · `POST /api/v1/conversions` |
| 域名 | `GET/POST /api/v1/domains` · `PUT/DELETE /api/v1/domains/{id}` |
| 账号 | `GET/POST /api/v1/users` · `PUT /api/v1/users/{id}` · `POST /api/v1/users/{id}/reset-password` |
| 安全 | `GET /api/v1/security/events` · `POST /api/v1/abuse/reports` |
| 跳转 / 探针 | `GET /{code}` · `GET /health` · `/health/ready` · `/health/info` |

交互式文档：`http://localhost:5080/scalar/v1`，OpenAPI：`/openapi/v1.json`。

统一 200 的补偿：前端 `ApiClient` 按 `code` 判定并处理 40101；span 打 `business.code` 标签；响应头预留 `X-Biz-Code`（CORS 已暴露）；下载文件名依赖 `Content-Disposition` 暴露。

### 跳转判定顺序（固定）

```
点击封顶 → 启停状态 → 生效时间 → 过期时间 → IP/机器人策略 → 访问密码 → 条件路由 → 防红中间页
```

封顶先于启停，是因为达阈值会自动停用，先判状态只会让访客看到「已停用」而不知道原因；密码先于路由，避免未通过校验的人通过命中分支探知目标地址。

### 前端页面

| 路由 | 说明 |
| --- | --- |
| `/` | 控制台：规模、趋势、热门短链、累计转化 |
| `/links` | 短链管理：筛选、批量操作、导出、二维码、条件路由 |
| `/links/{id}/stats` | 单链统计：趋势、维度、分支效果、明细导出 |
| `/domains` · `/security` · `/users` | 域名、安全事件、账号与角色 |

越权在服务端兜底（`11003` / `40301`），前端隐藏只是避免误操作。

## 部署要点

1. **Host 必须透传**：短链按 `Host + code` 解析，反向代理需配置 `proxy_set_header Host $host;`。
2. **多实例必须配 Redis**：否则短链更新最长滞后一个 TTL，限流退化为单机窗口。当前形态看 `/health/info` 的 `cache.backplane`。
3. **可信代理**：开启 `ReverseProxy:TrustForwardedHeaders` 时必须同时配置 `ReverseProxy:KnownProxies`，否则启动失败。
4. **探针分开**：存活接 `/health`，就绪接 `/health/ready`；把依赖放进存活探针会让数据库抖动升级为全体重启。
5. **上线前定统计时区**：`Analytics:TimeZone` 默认 `UTC`，`click_date` 写入时算好，切换不回溯历史。
6. **来源地默认信任请求头**：CDN 或反向代理必须剥离客户端同名头；公网部署建议配置 `GeoIp:DatabasePath` 使用离线库。
7. **MCP 默认关闭**：开启后等于多一个写入口，权限与令牌所属账号一致；公网暴露需收紧 `AllowedHosts` 与 `Mcp:PermitPerMinute`。

更完整的性能容量与运维说明见 `docs/architecture.md`。

## 目录结构

```
src/
  ShortLink.Contracts/                     信封、业务码、共享 DTO
  ShortLink.Domain/                        实体与业务规则（零框架依赖）
  ShortLink.Application/                   用例编排与端口接口
  ShortLink.Infrastructure/                EF Core、仓储、JWT、缓存、消息总线
  ShortLink.Infrastructure.Migrations.*/   Sqlite / MySql / PostgreSql 迁移
  ShortLink.Mcp/                           MCP 工具适配层
  ShortLink.Api/                           API 宿主与端点
  ShortLink.Web/                           Blazor WASM 管理后台
  ShortLink.AppHost/                       Aspire 本地编排
tests/
  ShortLink.UnitTests/                     领域规则与纯函数
  ShortLink.ArchitectureTests/             ArchUnitNET 分层约束
  ShortLink.IntegrationTests/              端到端集成测试
docs/
  architecture.md                          架构与概要设计
  database-design.md                       数据库设计与迁移
  feature-list.md                          功能清单
  features-mindmap.md                      架构与功能导图（Mermaid）
  roadmap.md                               路线图与技术债
  adr/  api/                               架构决策 / 业务码表
```

## 文档

| 文档 | 内容 |
| --- | --- |
| [architecture.md](docs/architecture.md) | 分层、请求链路、质量目标 |
| [database-design.md](docs/database-design.md) | 表结构、索引、多库差异 |
| [feature-list.md](docs/feature-list.md) | 按能力域的功能与实现位置 |
| [features-mindmap.md](docs/features-mindmap.md) | 架构与功能导图（Mermaid） |
| [roadmap.md](docs/roadmap.md) | 能力矩阵与技术债 |
| [biz-codes.md](docs/api/biz-codes.md) | 业务码表 |
| [adr/](docs/adr) | 关键设计决策记录 |
| [AGENTS.md](AGENTS.md) | 工程约定与架构约束 |
