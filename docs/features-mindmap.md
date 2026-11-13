# ShortLink · 架构与功能导图

用 Mermaid 描述架构与功能全景。功能条目与实现位置见 [feature-list.md](feature-list.md)，技术债见 [roadmap.md](roadmap.md)。

状态约定：未标注为已实现；`（有边界）` 表示已实现但有已知限制；`（未实现）` 表示规划中。

---

## 1. 技术选型

```mermaid
mindmap
  root((ShortLink 技术选型))
    后端运行时
      .NET 10
      ASP.NET Core Minimal API
      EF Core 10
      Asp.Versioning 路径版本
      Scalar 与 OpenAPI
      CPM 集中包管理
    前端
      Blazor WebAssembly
      MudBlazor 9
      独立静态站点
      ApiClient 按 code 判定
      QRCoder 浏览器内生成
    数据与缓存
      SQLite 默认
      MySQL 8 生产
      PostgreSQL 备选
      FusionCache 2.9
      Redis 可选背板
    消息
      Wolverine 6.64
      local 或 kafka
      500 条或 500ms
      逐队列 opt-in 持久化
    可观测性
      OpenTelemetry traces
      metrics
      logs
      Serilog
    AI 接入
      MCP Streamable HTTP
      9 个工具
      IsError 与真实 401
    来源地
      MaxMind GeoIP2 6.1
      请求头解析
```

## 2. 分层依赖

依赖严格单向，由 ArchUnitNET 在测试阶段守护。

```mermaid
flowchart TD
    Web["ShortLink.Web<br/>Blazor WASM · 仅依赖 Contracts"]
    Mcp["ShortLink.Mcp<br/>入口适配层 · 与 HTTP 端点平级"]
    Api["ShortLink.Api<br/>端点 / 认证 / 限流 / 异常收口"]
    Infra["ShortLink.Infrastructure<br/>EF Core / 仓储 / JWT / 缓存 / 消息"]
    App["ShortLink.Application<br/>用例编排 / 端口接口 / 校验"]
    Domain["ShortLink.Domain<br/>实体 / 聚合 / 领域规则"]
    Contracts["ShortLink.Contracts<br/>信封 / 业务码 / DTO"]

    Web -->|HTTP + Bearer JWT| Api
    Api --> Infra
    Mcp --> App
    Infra --> App
    App --> Domain
    Contracts -.-> Web
    Contracts -.-> Api
    Contracts -.-> Mcp
```

强制约束：

| 层 | 禁止引用 |
| --- | --- |
| `Domain` | EF Core、ASP.NET Core |
| `Application` | EF Core、`Microsoft.AspNetCore.Http` |
| `Infrastructure` | `ShortLink.Api` |
| `Api` | `ProblemDetails`（默认契约是统一信封） |
| `Mcp` | `DbContext`、仓储、`Infrastructure` |

## 3. 核心链路

### 3.1 跳转判定顺序

```mermaid
flowchart LR
    A["1 点击封顶"] --> B["2 启停状态"]
    B --> C["3 生效时间"]
    C --> D["4 过期时间"]
    D --> E["5 IP / 机器人策略"]
    E --> F["6 访问密码"]
    F --> G["7 条件路由 / 分流"]
    G --> H["8 防红中间页"]
```

- 封顶先于启停：达阈值会自动停用，先判状态只会让访客看到「已停用」而不知道原因。
- 密码先于路由：避免未通过校验的人通过命中分支探知目标地址。

### 3.2 缓存与点击管线

```mermaid
flowchart LR
    Req["请求 Host + code"] --> DC["域名缓存"]
    DC --> LC["短链快照缓存"]
    LC -->|未命中| DB[("数据库")]
    DB --> LC
    LC -->|不存在| NC["负缓存"]
    LC --> Redirect["302 / HTML 落地页"]
    Redirect -.->|异步发布| Bus["Wolverine 批处理"]
    Bus -->|500 条或 500ms| Write[("click_events + 计数器")]
```

- 一次跳转 = 一次缓存读取 + 一次消息发布，不查数据库。
- 短链快照包含规则、密码哈希与时间窗，路由选择是纯内存计算。
- 点击允许丢数据：发布失败只计指标，不阻塞跳转；点击永不进消息表。

### 3.3 统计、路由与归因

| 能力 | 设计要点 |
| --- | --- |
| 统计日界 | `click_date` 由 `IStatisticsCalendar` 计算，写入与读取共用同一日历 |
| 独立访客 | `SHA256(IP + UA + 统计日)` 截断，不可反推；日期与 `click_date` 同源 |
| 维度 TopN | 国家 / 省份、设备、浏览器、系统、语言 |
| 条件路由 | 8 个字段、9 个运算符、AND / OR 条件组、优先级与兜底 |
| A/B 分流 | 种子 `SHA256(visitorKey + linkId + priority)`，同一访客稳定命中 |
| 坏数据隔离 | 条件 JSON 解析失败按「该分支不命中」处理，不把短链打成 404 |
| 转化归因 | HMAC-SHA256 + 5 分钟时间窗，`(link_id, event_id)` 唯一索引幂等 |
| 保留策略 | 按 `clicked_at_utc` 水位分批删除，租约保证多副本互斥，默认关闭 |

## 4. 数据模型

10 张业务表 + 1 张 EF 迁移历史表。完整字段与索引见 [database-design.md](database-design.md)。

| 表 | 角色 | 关键约束 |
| --- | --- | --- |
| `short_links` | 短链聚合根 | `ux(domain_id, code)`；乐观并发令牌 |
| `short_domains` | 域名字典 | `ux(host)`；`ux(default_slot)` 保证至多一个默认域名 |
| `link_routes` | 条件分支 / A/B | `(link_id, priority)`；`ux(link_id, name)` |
| `click_events` | 点击明细，唯一线性增长表 | `ux(event_id)`；8 条索引覆盖统计、明细与清理 |
| `conversion_events` | 转化回调 | `ux(link_id, event_id)` 幂等 |
| `security_events` | 安全审计 | 工作区 / 短链 + 时间索引 |
| `users` | 账号与角色 | `ux(user_name)`；`token_version` 撤销旧令牌 |
| `workspaces` / `workspace_members` | 多租户与成员 | `ux(slug)`；`ux(workspace_id, user_id)` |
| `click_retention_leases` | 清理租约 | 主键 `name`，多副本互斥 |

设计要点：计数器与明细分离；明细按 `click_date` 聚簇；`event_id` 保证点击幂等；清理走 `(clicked_at_utc, id)` 索引。

## 5. 契约与横切

```mermaid
mindmap
  root((契约与横切))
    统一信封
      HTTP 200 + code / msg / data
      业务失败 data 为 null
      字段错误放 errors
    业务码
      只增不改
      BizCodes 单一来源
      11 用户 / 12 域名 / 13 短链
      14 路由 / 15 转化 / 16 安全 / 17 导出
    刻意的例外
      跳转短码路径保留真实状态码
      探针按状态码判定
      MCP 401 触发重新认证
    全 200 的补偿
      ApiClient 按 code 判定
      span 打 business.code
      响应头 X-Biz-Code
      CORS 暴露 Content-Disposition
    认证授权
      JWT HS256 + PBKDF2
      viewer / member / admin / owner
      ws 声明进 JWT
      LinkPolicy 单点判定
    限流
      auth 10/min 按 IP
      conversion 600/min 按 IP
      abuse 20/min 按 IP
      link-password 10/min 按 IP + 短码
      mcp 120/min 按调用者
      Redis Lua 原子计数
    探针
      /health 只证明进程活着
      /health/ready 查 DB 与缓存
      /health/info 是诊断口
    时区与来源地
      IStatisticsCalendar 唯一定义点
      默认 UTC，启动期校验
      IGeoLocationResolver 可插拔
      采集处不做 HtmlEncode
```

## 6. 部署形态

```mermaid
flowchart TD
    User["用户"] --> Edge["CDN / 反向代理<br/>Host 透传 · 剥离伪造头"]
    Edge --> Api1["Api 实例 1"]
    Edge --> ApiN["Api 实例 N"]
    Api1 <--> Redis[("Redis<br/>缓存背板 + 分布式限流")]
    ApiN <--> Redis
    Api1 --> DB[("MySQL / PostgreSQL")]
    ApiN --> DB
    Api1 -.->|可选| Kafka[("Kafka")]
    ApiN -.->|可选| Kafka
    Static["ShortLink.Web<br/>纯静态站点"] -.->|浏览器访问| Edge
```

| 形态 | 组成 |
| --- | --- |
| 单机 | SQLite + 本地队列 + 无 Redis |
| 多实例 | MySQL / PostgreSQL + Redis（缓存 + 限流） |
| 反代 / CDN | Host 透传、`ReverseProxy:KnownProxies`、剥离伪造头 |
| 事件流 | `Messaging:Transport=kafka`（broker 未联调） |
| 前端 | 纯静态托管，与 Api 独立扩缩容 |

## 7. 质量与决策

| 项 | 内容 |
| --- | --- |
| 测试分层 | 单测（领域 / 路由 / 指纹 / 时区 / 地理 / CSV）、集成（WebApplicationFactory + 真实 SQLite 迁移）、架构（ArchUnitNET） |
| 当前默认路径 | 212 通过 + 11 跳过（容器化用例需 `SHORTLINK_TEST_CONTAINERS=1`） |
| 异步校验 | `WaitUntilAsync` 轮询，不用固定 `Task.Delay` |
| ADR 0004 | 多数据库 provider 与迁移程序集分离 |
| ADR 0005 | 条件路由、A/B 分流与转化归因 |
| ADR 0006 | 限流分区、就绪探针与点击明细保留策略 |
| ADR 0007 | MCP Server 接入 |
| ADR 0008 | Wolverine 消息总线与点击不持久化 |
| ADR 0009 | 统计时区、缓存拓扑与来源地解析 |
| ADR 0010 / 0011 / 0012 | Aspire 本地编排 / compose 与 Helm 发布 / 容器化集成测试 |

已知边界：

- 迁移需要按 provider 分别生成，schema 变更要维护三套。
- 点击队列无容量上限，持续过载表现为内存增长。
- Kafka 路径未跑真实 broker，上线前必须联调。
- 来源地默认信任请求头，公网部署需在 CDN 层剥离客户端同名头。
- 离线库更新需重启，且没有过期告警。

---

## 8. 功能全景

### 8.1 短链主链路与跳转

```mermaid
mindmap
  root((A 短链与 B 跳转))
    A 短链主链路
      建链 / 编辑 / 删除
      自定义短码
      自动生成短码
      保留字保护
      启用 / 停用
      跳转类型 301 / 302 / 307 / 308
      有效期
      生效时间窗
      访问密码
      点击封顶
      目标地址扫描
      分页检索与排序
      短链详情
    B 跳转引擎
      单段短码路由
      多域名解析
      默认域名兜底
      三级缓存
      负缓存防穿透
      fail-safe
      固定判定顺序
      品牌化落地页
      防红中间页
    C 域名管理
      域名列表
      新增域名
      修改域名
      删除域名
      独立短码空间
```

### 8.2 统计、路由与转化

```mermaid
mindmap
  root((D 统计与 E / F 归因))
    D 统计与归因
      今日 / 累计点击
      独立访客
      按天趋势
      来源国 / 省份
      设备 / 浏览器 / 系统 / 语言
      最近访问明细
      分支效果
      工作区总览
      统计口径时区
      明细保留策略
      明细归档冷存储（未实现）
    E 条件路由与实验
      条件组 AND / OR
      8 个匹配字段
      9 个运算符
      优先级
      权重分流
      稳定分流
      命中试算
      坏数据隔离
      分支上限 20
      稳定分支 ID（有边界）
    F 转化归因
      转化密钥
      HMAC 回调
      幂等
      分支级归因
      转化价值累计
```

### 8.3 安全、运营与工程化

```mermaid
mindmap
  root((G 安全 / H 运营 / I 工程化))
    G 安全与治理
      IP 白名单
      IP 黑名单
      机器人策略
      安全事件台账
      匿名滥用举报
      四角色
      工作区隔离
      对象级权限
      账号管理
      登录与改密
    H 运营效率
      批量操作
      逐条结果码
      列表 CSV 导出
      点击明细 CSV 导出
      CSV 注入防护
      二维码
      归属与团队筛选
      全量导出分片（未实现）
    I 工程化与运维
      统一信封
      业务码表
      异常收口
      限流按分区键
      分布式限流
      探针三层
      缓存拓扑可见
      多数据库
      自动迁移
      OpenAPI 与 Scalar
      可观测性
      架构测试
      诊断端点
      日志降噪
```

### 8.4 消息、MCP 与前端

```mermaid
mindmap
  root((J 消息 / K MCP / L 前端))
    J 消息总线
      点击批处理
      点击不进消息表
      封顶审计持久化
      传输可切换
      配置启动期校验
      Kafka 真实集群验证（未实现）
    K MCP
      shortlink_create
      shortlink_get
      shortlink_list
      shortlink_set_enabled
      shortlink_delete
      shortlink_stats
      shortlink_dashboard
      shortlink_recent_clicks
      shortlink_list_domains
      IsError 语义
      工具级鉴权
      按 sub 限流
      OAuth 2.1（未实现）
    L 前端
      /login
      控制台
      /links
      /links/:id/stats
      /domains
      /security
      /users
      统一 ApiClient
      FileSaver
      12 个业务组件
```

功能条目统计与实现位置见 [feature-list.md](feature-list.md)。
