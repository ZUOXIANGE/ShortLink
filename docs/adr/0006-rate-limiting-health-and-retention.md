# ADR 0006：限流分区、就绪探针与点击明细保留策略

- 状态：已采纳
- 日期：2026-11-11
- 相关：`src/ShortLink.Api/Infrastructure/RateLimitingSetup.cs`、`src/ShortLink.Infrastructure/RateLimiting/DistributedWindowRateLimiter.cs`、`src/ShortLink.Infrastructure/Analytics/ClickRetentionService.cs`、`src/ShortLink.Api/Endpoints/HealthEndpoints.cs`

## 背景

P1/P2 完成后，功能面已经齐了，但三处工程的「默认值」在真实部署下会直接出事：

1. **限流形同虚设**。早期用 `AddFixedWindowLimiter("auth")` 注册登录限流，那是**全进程共享的一个窗口**：任何人对着登录接口刷 10 次，其他所有用户在一分钟内都进不来。这是可用性问题，不是性能问题。多实例部署时更糟——实际配额变成「阈值 × 实例数」，10 次/分钟的策略在 4 个实例下等于 40 次/分钟。
2. **`click_events` 没有回收口径**。它是平台里唯一线性增长、且没有任何业务方会回头修改的表。不设保留策略时，一亿行会让统计查询的索引扫描和备份窗口同时失控，而这类问题通常在上线半年后才被发现。
3. **就绪探针太浅**。只 `CanConnect` 了数据库。Redis 挂掉时实例仍然报告就绪并继续接流量，而短链缓存失效会让每个请求都打到主库——本该摘掉一个实例，结果变成全站雪崩。

## 决策

### 1. HTTP 限流按来源 IP 分区（特殊策略单独分区）

分区键为 `{policy}:{RemoteIpAddress}`，通过 `RateLimitPartition.Get(partitionKey, factory)` 生成。选 `Get` 而非 `GetFixedWindow` 是因为要在工厂里根据「是否配置 Redis」动态决定用哪个实现。

后续补充的两个策略按各自语义分区，而不是一律按 IP：

- `link-password` 按「来源 IP + 短码」分区，避免单条短链被同一出口 IP 爆破。
- `mcp` 按 JWT `sub` 分区，AI 客户端常从同一出口 IP 批量调用，按 IP 会把一个团队算成一个人。

代价与约束：未开启 `ReverseProxy:TrustForwardedHeaders` 时 `RemoteIpAddress` 是代理 IP，所有用户会共享一个桶。这是**必须开启**该配置的直接原因，已写进 README 部署章节。

### 2. 分布式计数用单条 Lua 脚本，不做「先 INCR 再 EXPIRE」

```lua
local current = redis.call('INCRBY', KEYS[1], permits)
local ttl = redis.call('PTTL', KEYS[1])
if ttl < 0 then redis.call('PEXPIRE', KEYS[1], window) ttl = window end
if current > limit then return {0, ttl} end
return {1, 0}
```

不用两步 `INCR` + `EXPIRE` 的原因很具体：进程在两条命令之间崩溃、或 Redis 主从切换丢掉 `EXPIRE`，都会留下一个**没有 TTL 的计数器**。那个 IP 就永久被限流了，且没有任何自动恢复机制——运维只能手工 `DEL`。单脚本保证「加计数」与「设过期」原子完成。

`PTTL < 0` 分支同时覆盖「键不存在」（返回 -2）和「键存在但无过期」（返回 -1）两种状态，两种都要补设过期。

### 3. Redis 故障时回落本地窗口，而不是放行

`DistributedWindowRateLimiter` 在 `RedisConnectionException` / `RedisTimeoutException` 时切换到进程内 `FixedWindowRateLimiter`，并累加 `shortlink.ratelimit.fallbacks`。三种可选策略里：

- 放行：限流在故障期间完全失效，攻击者只要打挂 Redis 就绕过所有限制。
- 全拒：Redis 抖动直接变成全站不可用。
- 回落本地：保护仍在（单机维度），可用性不受影响，用指标暴露「这段时间配额不准」。

选第三种。指标是刻意加的——没有它，回落会静默发生，多实例配额悄悄变成单机配额而没人知道。

`IdleDuration => Window * 2` 是配套细节：`RateLimiter` 按分区键缓存在内存字典里，按 IP 分区意味着分区数等于活跃 IP 数。不设空闲回收，这个字典会在长时间运行后成为内存泄漏点。

### 4. 存活与就绪分离，语义不混用

| 端点 | 检查内容 | 失败后果 |
| --- | --- | --- |
| `/health` | 仅进程自身 | 重启容器 |
| `/health/ready` | 数据库 + 缓存 | 从负载均衡摘除 |

`/health` 刻意**不查数据库**。把数据库连通性放进存活探针是最常见的配置错误：数据库抖动 30 秒，K8s 会把全部实例一起重启，重启期间旧连接还没释放、新实例又抢不到连接，故障被放大成雪崩。

`/health/ready` 的失败响应体是 `database=ok,cache=fail` 这种纯文本，而不是只有 503 状态码——出问题时能一眼看出是哪个依赖挂了，不用再去翻日志。

`ICacheHealthProbe` 在未注册 `IConnectionMultiplexer` 时恒返回健康：没配 Redis 就是「不需要缓存」，不是「缓存不可用」。这条让 SQLite 单机部署不会被误判。

### 5. 保留策略默认关闭，且只删不归档

`Analytics:Retention:Enabled` 默认 `false`。理由：删除不可逆，任何默认开启的数据删除都是给运维埋雷。开启后：

- 过滤字段用 `ClickedAtUtc` 而非 `ClickDate`：前者是 `(clicked_at_utc, id)` 索引前缀，且不随「按天统计口径」的调整而改变含义。
- **先取主键再按主键删**，不复用一条 `DELETE ... WHERE clicked_at_utc < @cutoff`：千万行表上的单条大 DELETE 会长时间持锁，复制延迟直接飙升。分批短事务对主库友好得多。
- 三个上限：`BatchSize`（单批行数，受 SQL 变量上限约束，clamp 到 50~2000）、`MaxRowsPerRun`（单次总量，防止一次清理把库压满）、`BatchDelayMilliseconds`（批间停顿，给复制留追平时间）。
- 按 UTC 日期去重（`today != lastRunDate`）：进程重启不会重复跑，也不会因为 15 分钟轮询而一天清理 96 次。

`PurgeAsync` 是 `public` 的，`ExecuteAsync` 只是它的调度外壳。这样集成测试可以直接驱动一次清理并断言删除行数，不需要等定时器、改系统时间或 mock 时钟。

## 后果

- 多实例部署下配额准确，但**必须**配置 Redis；未配置时只有单机配额，已在 README 标明。
- 反代部署下 `ReverseProxy:TrustForwardedHeaders=true` 从「建议」升级为「必须」。
- `click_events` 有了明确的增长上界（保留天数 × 日增），容量规划可以算出来。
- 探针配置错误的风险转移到了部署侧文档，代码侧不再提供「一个端点全搞定」的捷径。
- 尚未实现：明细**归档**（导出到对象存储后再删）。当前只有删除，这对需要长期留存归因数据的场景不够，已列在 `docs/roadmap.md` 的未实现项中。
