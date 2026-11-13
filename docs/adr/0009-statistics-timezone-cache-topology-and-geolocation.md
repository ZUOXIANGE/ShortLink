# ADR 0009：统计时区、缓存失效范围与来源地解析

- 状态：已采纳
- 日期：2026-11-11
- 相关：`src/ShortLink.Application/Abstractions/TimeAndIdentity.cs`、`src/ShortLink.Application/Abstractions/ICacheHealthProbe.cs`、`src/ShortLink.Application/Abstractions/IGeoLocationResolver.cs`、`src/ShortLink.Infrastructure/Time/StatisticsCalendar.cs`、`src/ShortLink.Infrastructure/Caching/CacheTopology.cs`、`src/ShortLink.Infrastructure/Geo/`、`src/ShortLink.Infrastructure/Options/Options.cs`

## 背景

路线图「已知技术债」1/2/3 都不属于「功能没做完」，而是**口径没定义**：系统能跑，但在特定部署形态下会给出错误或误导性的结果。

| 技术债 | 真实症状 | 为什么不能一直挂着 |
| --- | --- | --- |
| 统计口径时区 | `click_date = DateOnly.FromDateTime(clickedAtUtc)` 硬编码 UTC 日界。东八区运营者看到的「今日」比实际晚 8 小时；23:30 的点击算到前一天 | 数据一旦按错的日界写进 `click_date`，后面加多少张报表都救不回来——分桶错了就是错了 |
| 缓存失效范围 | 未配 Redis 时，停用一条短链只失效本实例缓存，其他实例最长 60 秒仍在跳转。日志里没有任何提示 | 这不是性能问题而是**正确性问题**：用户点了「停用」，短链还在别处可访问，且无人知道 |
| 地理信息 | 只读 `CF-IPCountry` / `X-Country`，没有 IP 库；且**取值处做了 HtmlEncode** | 条件路由按国家分流因此永不命中（编码后的值与规则里的值不相等），是个隐性死分支 |

三条的共同点是：**默认行为看起来正常，出问题时静默**。

## 决策

### 1. 统计口径统一走 `IStatisticsCalendar`，默认 UTC

新增端口接口，读写两侧共用同一份日历：

```csharp
public interface IStatisticsCalendar
{
    string TimeZoneId { get; }
    DateOnly Today { get; }
    DateOnly ToDate(DateTime utcInstant);
}
```

- **写入侧**：`ClickEvent.Create(..., DateOnly clickDate, ...)` 由调用方传入，领域层不再自己 `DateOnly.FromDateTime`——领域层没有时区知识，也不该有。
- **读取侧**：`AnalyticsService.Range()` 的「今日」用 `calendar.Today`，趋势区间按同一日历切日。
- **配置**：`Analytics:TimeZone`，默认 `UTC`。**默认值刻意等于旧行为**，升级不改变任何历史分桶。
- **启动期校验**：时区为空或 `FindSystemTimeZoneById` 失败直接抛异常，不等到第一次查询才 500。同时支持 IANA（`Asia/Shanghai`）与 Windows（`China Standard Time`）两种 id。

`ToDate` 处理三种 `DateTimeKind`：`Utc` 直接换算、`Local` 先转 UTC、`Unspecified` **视为 UTC**——数据库读出来的 `DateTime` 都是 `Unspecified`，如果当成服务器本地时间，同一份数据在不同机器上会算出不同的日期。

### 2. 缓存失效收口为「可观测的部署约束」，不做跨实例广播

明确**不实现**失效广播（Redis pub/sub 或多播），理由：

- 广播要解决的是「多实例一致性」，而多实例本来就必须配 Redis（限流也依赖它）。背板在的时候 FusionCache 的背板通知已经解决了失效传播；背板不在的时候搞一套独立的广播通道，等于在「没有共享基础设施」的前提下硬造共享状态。
- 更关键的是**失败模式**：广播丢一条、订阅端阻塞、网络分区，都会得到「部分实例失效、部分没失效」这种比「全部滞后一个 TTL」更难排查的状态。

改为把事实暴露出来：

```jsonc
// GET /health/info
{
  "cache": {
    "backplane": "in-process",          // 或 "redis"
    "crossInstanceInvalidation": false, // 与 backplane 同义，但写成人话给值班看
    "linkTtlSeconds": 60
  }
}
```

- `ICacheTopology` 单例从 `CacheOptions.RedisConnectionString` 判定背板，端点与启动日志共用同一份判断。
- 启动日志：无背板时 `LogCacheTopology` 打一行。在 Kubernetes 环境（`KUBERNETES_SERVICE_HOST` 存在）打 `Warning`，单机部署打 `Information`——同一件事实，两种部署形态的风险等级不同。
- 结果：从「静默降级」变成「明确不支持」。单实例无 Redis 依然是合法配置，不再是一个没人知道的口子。

### 3. 来源地抽象为 `IGeoLocationResolver`，默认请求头、可选离线库

两套实现，切换只看有没有配库：

| 实现 | 触发条件 | 取值来源 |
| --- | --- | --- |
| `HeaderGeoLocationResolver` | 默认（`GeoIp:DatabasePath` 为空） | 按 `CountryHeaders` / `ProvinceHeaders` 顺序取第一个非空请求头 |
| `MaxMindGeoLocationResolver` | 配了 `GeoIp:DatabasePath` | GeoLite2 City/Country mmdb，按 `Locales` 取显示名；查不到或异常**回落请求头解析器** |

几个刻意的取舍：

- **头名可配**。CDN 头五花八门（`CF-IPCountry`、`X-Country`、`X-Geo-Country`…），写死等于把部署方案锁死一家厂商。
- **采集处不做 HtmlEncode**。这是一类容易被忽略的隐性缺陷：入库前编码，条件路由拿编码后的值跟规则比较，永远不相等。编码属于**渲染**职责，谁输出到 HTML 谁负责。
- **库路径配了但文件不存在 → 启动失败**。静默回落请求头的表现是「配了库但省份还是空」，排查成本远高于起不来。
- **IP 查不到回落请求头**。内网地址、`127.0.0.1`、库覆盖不到的 IP 走不通 mmdb，此时请求头是唯一的补充来源，不是二选一。

## 后果

**正面**

- 三个静默缺陷变成「可配、可观测、有默认值」。默认配置下行为与升级前完全一致，无需迁移。
- 统计口径有了唯一定义点，新增报表不会再各自 `DateTime.Today`。
- 来源地解析可替换（换 CDN、加库、改头名）不需要动跳转端点。

**负面 / 需要接受的边界**

- **改时区不回溯**：`click_date` 是写入时算好的，切换时区后历史数据仍按旧口径分桶。要重算必须离线重刷（见 `docs/roadmap.md` 偿还记录）。
- **请求头信任边界**：头解析器信任调用方，公网裸暴露时可被伪造，条件路由按国家分流也会被绕过。必须在 CDN/反代层剥离客户端同名头。
- **mmdb 需自行维护**：仓库不内置库文件；替换文件要重启，且没有过期告警（GeoLite2 按月发布）。
- **`IStatisticsCalendar` 是单例**：时区在进程生命周期内固定，改配置需重启。接受这一点换取「热路径上不做时区查找」。

## 备选方案

| 方案 | 否决原因 |
| --- | --- |
| 时区固定 UTC，让前端换算 | 聚合查询发生在服务端（`GROUP BY click_date`），前端换算救不了分桶错误；「今日点击」的口径无法在展示层修复 |
| 每条点击都存 UTC 时间戳、查询时再按配置换算 | 需要每次查询对 `click_events` 做 `CONVERT_TZ`，索引失效且无法预聚合；本项目的统计靠 `click_date` 走索引 |
| 用 Redis pub/sub 做跨实例失效广播 | 见决策 2：在「没有共享基础设施」的前提下造共享状态，失败模式更差 |
| 引入完整的 GeoIP 服务（如 ip2region HTTP 服务） | 跳转热路径多一跳网络调用；离线库是纯内存查表，成本差两个数量级 |
| 直接把 mmdb 打进镜像 | 库文件按月更新，会把「换库」变成「重新发布镜像」；且 MaxMind 许可要求使用者自行接受条款 |
