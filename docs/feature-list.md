# ShortLink · 功能清单

按能力域分组，与 `docs/roadmap.md`（路线图）、`docs/features-mindmap.md`（架构与功能导图）互为索引。
状态标记：✅ 已实现并测试覆盖 ｜ 🟡 已实现但有已知边界 ｜ ⬜ 未实现。

---

## A. 短链主链路（P0）

| 编号 | 功能 | 状态 | 入口 / 实现 | 说明 |
| --- | --- | --- | --- | --- |
| A01 | 创建短链 | ✅ | `POST /api/v1/links` · `LinkService.CreateAsync` | 目标地址、标题、备注、跳转类型、时间窗、密码、封顶 |
| A02 | 自定义短码 | ✅ | `CreateLinkRequest.Code` · `ShortCode` | 校验字符集与长度，保留字保护 |
| A03 | 自动生成短码 | ✅ | `ShortCode.Generate` | 加密随机 Base62，冲突自动加长重试 |
| A04 | 短码保留字保护 | ✅ | `LinkCodeReserved` 13003 | `api`/`health`/`_framework` 等不可占用 |
| A05 | 编辑短链 | ✅ | `PUT /api/v1/links/{id}` | 全量覆盖语义，改后主动失效缓存；乐观并发冲突返回 `40901` |
| A06 | 启用 / 停用 | ✅ | `POST /api/v1/links/{id}/toggle` | 幂等，停用可再启用 |
| A07 | 删除短链 | ✅ | `DELETE /api/v1/links/{id}` | 永久删除，级联删分支与转化 |
| A08 | 跳转类型 | ✅ | `LinkRedirectType` | 301/302/307/308，307/308 保持请求方法 |
| A09 | 有效期 | ✅ | `ExpiresAtUtc` | 过期返回 410 落地页 |
| A10 | 生效时间窗 | ✅ | `StartsAtUtc` | 未到生效时间返回「未生效」态 |
| A11 | 访问密码 | ✅ | `PasswordHash`（PBKDF2） | HTML 表单验证，通过后放行 |
| A12 | 点击封顶 | ✅ | `MaxClicks` | 达阈值同事务内自动停用 + 安全事件 |
| A13 | 目标地址扫描 | ✅ | `LinkTargetRejected` 13013 | 拦截 `javascript:`、内网、自引用等 |
| A14 | 分页检索与排序 | ✅ | `GET /api/v1/links?…` | 关键词、状态、域名、归属、`status=capped` |
| A15 | 短链详情 | ✅ | `GET /api/v1/links/{id}` | 含完整配置与统计摘要 |

## B. 跳转引擎

| 编号 | 功能 | 状态 | 入口 / 实现 | 说明 |
| --- | --- | --- | --- | --- |
| B01 | 单段短码路由 | ✅ | `GET/POST /{code}` | 必须最后注册，避免吞掉前缀路由 |
| B02 | 多域名解析 | ✅ | `Host + code` | 域内独立短码空间，Host 不匹配回落默认域名 |
| B03 | 三级缓存路径 | ✅ | `FusionLinkCache` | 域名缓存 → 短链缓存 → 回源 |
| B04 | 负缓存防穿透 | ✅ | 同上 | 不存在的短码缓存空值 |
| B05 | fail-safe | ✅ | 同上 | 回源失败用过期快照顶住 |
| B06 | 固定判定顺序 | ✅ | `RedirectService` / `AccessPolicy` | 封顶→启停→生效→过期→IP/机器人→密码→路由→防红 |
| B07 | 品牌化落地页 | ✅ | `RedirectPageRenderer` | 停用/过期/不存在/需密码，返回真实状态码 |
| B08 | 防红中间页 | ✅ | 微信/QQ UA 识别 | `?direct=1` 跳过 |
| B09 | 不阻塞跳转 | ✅ | 消息发布 | 发布失败只计指标，不影响跳转 |

## C. 域名管理

| 编号 | 功能 | 状态 | 入口 / 实现 | 说明 |
| --- | --- | --- | --- | --- |
| C01 | 域名列表 | ✅ | `GET /api/v1/domains` | 含是否默认、短链数量 |
| C02 | 新增域名 | ✅ | `POST /api/v1/domains` | Host 唯一校验 |
| C03 | 修改域名 | ✅ | `PUT /api/v1/domains/{id}` | Host 不可改（改 Host 等于换域） |
| C04 | 删除域名 | ✅ | `DELETE /api/v1/domains/{id}` | 有短链时拒绝（`DomainInUse`） |
| C05 | 默认域名兜底 | ✅ | `Seed:DefaultDomainHost` | Host 无法匹配时使用；唯一槽位索引保证全库至多一个默认域名 |

## D. 统计与归因

| 编号 | 功能 | 状态 | 入口 / 实现 | 说明 |
| --- | --- | --- | --- | --- |
| D01 | 今日 / 累计点击 | ✅ | `GET /links/{id}/stats` | 计数器增量更新，不扫明细 |
| D02 | 独立访客（UV） | ✅ | `VisitorFingerprint` | `SHA256(IP|UA|统计日)`，不可反推 |
| D03 | 按天趋势 | ✅ | `days` 参数 | 区间由 `IStatisticsCalendar` 切分 |
| D04 | 来源国 / 省份 | ✅ | `IGeoLocationResolver` | 请求头或 MaxMind 离线库 |
| D05 | 设备 / 浏览器 / 系统 / 语言 | ✅ | `UserAgentParser` | TopN 维度 |
| D06 | 最近访问明细 | ✅ | `GET /links/{id}/clicks` | HTTP 返回来源 IP；MCP 投影不返回原始 IP |
| D07 | 分支效果统计 | ✅ | 同上 | 分支点击/转化/转化率/转化价值 |
| D08 | 工作区总览 | ✅ | `GET /api/v1/dashboard/overview` | 规模、趋势、Top 5、累计转化 |
| D09 | 统计口径时区 | ✅ | `Analytics:TimeZone` | 默认 UTC；写读同源 |
| D10 | 明细保留策略 | ✅ | `ClickRetentionService` | 默认关闭，分批删除，不可逆 |
| D11 | 明细归档到冷存储 | ⬜ | — | 当前只删不归档 |

## E. 条件路由与实验

| 编号 | 功能 | 状态 | 入口 / 实现 | 说明 |
| --- | --- | --- | --- | --- |
| E01 | 条件路由 | ✅ | `GET/PUT /links/{id}/routes` | 全量覆盖保存 |
| E02 | 8 个匹配字段 | ✅ | `RouteFields` | country/province/device/browser/os/language/referer/query |
| E03 | 9 个运算符 | ✅ | `RouteOperators` | eq/neq/contains/not_contains/prefix/in/not_in/exists/not_exists |
| E04 | AND / OR 条件组 | ✅ | `RouteMatchMode` | JSON 存 `conditions_json` |
| E05 | 优先级 | ✅ | `-100..100` | 高优先级先命中 |
| E06 | A/B 权重分流 | ✅ | `weight`（≤1000） | 种子 `SHA256(visitorKey|linkId|priority)`，同访客稳定 |
| E07 | 命中试算 | ✅ | `POST /links/{id}/routes/test` | 返回命中分支与分流比例，不产生点击 |
| E08 | 坏数据隔离 | ✅ | `RouteConditions.TryParse` | 解析失败按不命中，不打成 404 |
| E09 | 分支数上限 | ✅ | `RouteLimitExceeded` | 单链 20 个分支；同链分支名唯一（`14003`） |
| E10 | 稳定分支 ID | 🟡 | — | 当前全量覆盖会导致 ID 变化，历史只能按名称归集（技术债 6） |

## F. 转化归因

| 编号 | 功能 | 状态 | 入口 / 实现 | 说明 |
| --- | --- | --- | --- | --- |
| F01 | 获取转化密钥 | ✅ | `GET /links/{id}/conversion-key` | 首次生成，可重复获取 |
| F02 | HMAC 回调 | ✅ | `POST /api/v1/conversions` | `X-Timestamp` + `X-Signature: sha256=…`，5 分钟容差 |
| F03 | 幂等 | ✅ | `ux(link_id, event_id)` | 重复回调不重复计数 |
| F04 | 分支级归因 | ✅ | `conversion_events.route_id` | 支撑分支转化率与价值 |
| F05 | 转化价值 | ✅ | `value` 字段 | 累计与按分支汇总 |

## G. 安全与治理

| 编号 | 功能 | 状态 | 入口 / 实现 | 说明 |
| --- | --- | --- | --- | --- |
| G01 | IP 白名单 | ✅ | `ip_allow_list`（含 CIDR） | 内存匹配 |
| G02 | IP 黑名单 | ✅ | `ip_block_list` | 同上 |
| G03 | 机器人策略 | ✅ | `allow`/`count_only`/`block` | 按 UA 判定 |
| G04 | 安全事件台账 | ✅ | `GET /api/v1/security/events` | manager 及以上可见 |
| G05 | 匿名滥用举报 | ✅ | `POST /api/v1/abuse/reports` | 按 IP 限流 20/min |
| G06 | 多用户与角色 | ✅ | `UserRoles` | viewer/member/admin/owner |
| G07 | 工作区隔离 | ✅ | JWT `ws` 声明 | 列表/统计/导出全部按工作区过滤 |
| G08 | 对象级权限 | ✅ | `LinkPolicy.CanModify` | 单一判定点 |
| G09 | 账号管理 | ✅ | `/api/v1/users` | 新建、改角色、启停、重置密码 |
| G10 | 登录与改密 | ✅ | `/api/v1/auth/*` | JWT + PBKDF2，登录限流 10/min/IP；改密/重置/停用/降权递增 `token_version` 撤销旧令牌 |

## H. 运营效率

| 编号 | 功能 | 状态 | 入口 / 实现 | 说明 |
| --- | --- | --- | --- | --- |
| H01 | 批量启停/删除/改域名/改时间窗 | ✅ | `POST /api/v1/links/batch` | 单事务，逐条结果码，上限 500 |
| H02 | 短链列表 CSV 导出 | ✅ | `GET /api/v1/links/export` | 流式，跟随筛选，UTF-8 BOM |
| H03 | 点击明细 CSV 导出 | ✅ | `GET /links/{id}/clicks/export` | 同上，10 万行硬顶 |
| H04 | CSV 注入防护 | ✅ | `ExportService` | `= + - @` 前缀阻断 |
| H05 | 二维码 | ✅ | `QrDialog` · QRCoder | 浏览器内生成，8/12/20 px/module，内容不出设备 |
| H06 | 归属与团队筛选 | ✅ | `mine` / `createdByUserId` | 配合角色权限 |
| H07 | 控制台总览页 | ✅ | `/` | 规模、趋势、热门、转化 |
| H08 | 全量导出无上限 | ⬜ | — | 需按时间分片（技术债 8） |

## I. 工程化与运维

| 编号 | 功能 | 状态 | 入口 / 实现 | 说明 |
| --- | --- | --- | --- | --- |
| I01 | 统一信封 | ✅ | `ApiResponse` | 全 200 + `{code,msg,data,errors?}` |
| I02 | 业务码表 | ✅ | `BizCodes` + `docs/api/biz-codes.md` | 分段，只增不改 |
| I03 | 异常收口 | ✅ | `GlobalExceptionHandler` | 唯一入口，含框架层归一化 |
| I04 | 限流按分区键 | ✅ | `RateLimitPartition.Get` | `auth` / `conversion` / `abuse` 按 IP，`link-password` 按 IP + 短码，`mcp` 按调用者 |
| I05 | 分布式限流 | ✅ | `DistributedWindowRateLimiter` | Redis Lua 原子计数，故障回落本地 |
| I06 | 存活探针 | ✅ | `GET /health` | 不查外部依赖 |
| I07 | 就绪探针 | ✅ | `GET /health/ready` | DB + 缓存，503 带明细 |
| I08 | 运行态诊断 | ✅ | `GET /health/info` | 缓存拓扑 + 统计时区 |
| I09 | 缓存拓扑可见 | ✅ | `ICacheTopology` + 启动日志 | 无背板时 K8s 打 Warning |
| I10 | 多数据库 | ✅ | `DatabaseOptions.Provider` | SQLite / MySQL / PostgreSQL |
| I11 | 自动迁移 | ✅ | `DatabaseInitializer` | 启动时 `MigrateAsync` |
| I12 | OpenAPI + Scalar | ✅ | `/openapi/v1.json`、`/scalar/v1` | API 版本化 |
| I13 | 可观测性 | ✅ | OTel traces/metrics/logs | 业务码打点、跳转结果分布 |
| I14 | 健康检查过滤 | ✅ | `AddAspNetCoreInstrumentation` | `/health` 不进 trace，免噪声 |
| I15 | 架构测试 | ✅ | ArchUnitNET | 分层依赖与契约约束 |

## J. 消息总线

| 编号 | 功能 | 状态 | 入口 / 实现 | 说明 |
| --- | --- | --- | --- | --- |
| J01 | 点击批处理 | ✅ | Wolverine `BatchMessagesOf<ClickRecorded>` | 500 条 / 500ms；按 `event_id` 幂等写入 |
| J02 | 点击不进消息表 | ✅ | 逐队列 `BufferedInMemory()` | 高频数据不做持久化 |
| J03 | 封顶审计持久化 | ✅ | `LinkClickCapped` + `UseDurableInbox()` | 低频事件 opt-in |
| J04 | 传输可切换 | ✅ | `Messaging:Transport` | `local` / `kafka` |
| J05 | 配置启动期校验 | ✅ | `MessagingOptions.Validate` | 缺 broker、topic 前缀空直接失败 |
| J06 | Kafka 生产验证 | ⬜ | — | 未跑真实 broker（技术债 14） |

## K. MCP（AI 客户端接入）

| 编号 | 工具 / 功能 | 状态 | 权限 | 说明 |
| --- | --- | --- | --- | --- |
| K01 | `shortlink_create` | ✅ | member | 建短链，code 留空则生成 |
| K02 | `shortlink_get` | ✅ | viewer | 按 ID 或短链地址查详情 |
| K03 | `shortlink_list` | ✅ | viewer | 分页，可按关键词/状态筛选 |
| K04 | `shortlink_set_enabled` | ✅ | member | 启停，幂等 |
| K05 | `shortlink_delete` | ✅ | member | 标注 `Destructive=true` |
| K06 | `shortlink_stats` | ✅ | viewer | 单链统计 + 分支效果 |
| K07 | `shortlink_dashboard` | ✅ | viewer | 工作区总览 + Top 5 |
| K08 | `shortlink_recent_clicks` | ✅ | viewer | 最近明细，不含原始 IP |
| K09 | `shortlink_list_domains` | ✅ | viewer | 域名选项 |
| K10 | 工具错误语义 | ✅ | — | `IsError=true`，不套信封 |
| K11 | 工具级鉴权 | ✅ | — | 方法内二次角色校验 |
| K12 | 按 `sub` 限流 | ✅ | — | 120/min，按调用者而非 IP |
| K13 | MCP OAuth 2.1 | ⬜ | — | 仅支持静态 Bearer JWT |

## L. 前端（Blazor WASM）

| 编号 | 页面 / 组件 | 状态 | 说明 |
| --- | --- | --- | --- |
| L01 | `/login` | ✅ | 独立布局，失败统一按业务码提示 |
| L02 | `/`（控制台） | ✅ | 规模卡片、趋势图、热门短链、累计转化 |
| L03 | `/links` | ✅ | 筛选、批量、CSV、二维码、路由、转化密钥 |
| L04 | `/links/{id}/stats` | ✅ | 趋势、维度、分支效果、明细导出 |
| L05 | `/domains` | ✅ | 域名维护 |
| L06 | `/security` | ✅ | 安全事件台账（admin+） |
| L07 | `/users` | ✅ | 账号与角色（admin+） |
| L08 | 统一 `ApiClient` | ✅ | 按 `code` 判定，40101 自动跳登录 |
| L09 | `FileSaver` | ✅ | Blob 下载，依赖 CORS 暴露 `Content-Disposition` |
| L10 | 12 个业务组件 | ✅ | 编辑、批量、二维码、路由编辑、趋势、维度列表等 |

---

## 统计

| 域 | 已实现 | 有条件实现 | 未实现 |
| --- | --- | --- | --- |
| A 短链主链路 | 15 | 0 | 0 |
| B 跳转引擎 | 9 | 0 | 0 |
| C 域名 | 5 | 0 | 0 |
| D 统计与归因 | 10 | 0 | 1 |
| E 条件路由 | 9 | 1 | 0 |
| F 转化 | 5 | 0 | 0 |
| G 安全与治理 | 10 | 0 | 0 |
| H 运营效率 | 7 | 0 | 1 |
| I 工程化 | 15 | 0 | 0 |
| J 消息总线 | 5 | 0 | 1 |
| K MCP | 12 | 0 | 1 |
| L 前端 | 10 | 0 | 0 |
| **合计** | **112** | **1** | **4** |

> 未实现的 4 项是 D11 明细归档、H08 全量导出分片、J06 Kafka 真实集群联调、K13 MCP OAuth 2.1。
> 另有 roadmap 中的两项（OIDC 登录、安装向导）尚未进入本期功能范围，记在 `docs/roadmap.md`。
