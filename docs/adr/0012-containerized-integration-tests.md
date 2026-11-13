# ADR 0012：容器化集成测试的门禁设计

- 状态：已采纳
- 日期：2026-11-12
- 相关：`tests/ShortLink.IntegrationTests/ContainerFixtures.cs`、`ContainerizedSmokeTests.cs`、`DistributedRateLimiterTests.cs`、`Directory.Packages.props`

## 背景

审查测试覆盖时发现两个 P1 盲区：

| 盲区 | 症状 | 为什么一直没被发现 |
| --- | --- | --- |
| MySQL 零覆盖 | 三个 migrations 程序集各自独立，MySQL 方言（`ExecuteUpdateAsync`、索引、`utf8mb4`）在 CI 里从未执行 | 所有集成测试都跑 SQLite，而 SQLite 能过就等于「功能是对的」 |
| PostgreSQL 零覆盖 | 第三份迁移程序集同样从未执行；PG 对标识符大小写、identity 列、索引名的处理与 MySQL 不同 | 同上，而且「支持多 provider」只是配置里的一行字符串，没人验过它真的能用 |
| Redis 零覆盖 | FusionCache 背板、`DistributedWindowRateLimiter` 的 Lua 原子窗口、Redis 探针从未被执行 | `ApiFactory` 显式 `Cache:RedisConnectionString = ""`；而且把 `RateLimit:*PermitPerMinute` 抬到 1000，连本地限流也顺手关掉了 |

这两块恰好是「只有部署后才第一次运行」的典型：本机手工冒烟能发现，但不可重复、不会在回归时提醒任何人。实际案例：Aspire 的 Redis 容器默认 `requirepass`，连接串少 `password=` 时启动日志一切正常，只有 `/health/ready` 的 cache 一直 fail——没有任何自动化手段能拦住它。

## 决策

### 1. 容器化用例默认关闭，用环境变量门禁

```bash
SHORTLINK_TEST_CONTAINERS=1 dotnet test tests/ShortLink.IntegrationTests -c Debug
```

- 不引入 `[Trait]` + 运行时过滤方案：过滤靠调用方记得加参数，忘了就是"跑了但没跑那些"；门禁写在代码里，默认行为是 `Assert.Skip`，跳过数会出现在摘要里。
- **未开启时必须 Skip，不能 return 假装通过**。跳过是可见的，静默通过是隐形的。
- 不用 Testcontainers 的 Docker 可用性探测做门禁：探测本身要超时、要区分"没装 Docker"与"Docker 卡住"，收益不抵复杂度；显式开关的语义更明确。

### 2. 一个 fixture 同时持有容器与宿主

xunit 的 collection fixture **之间不能互相注入**，所以 `容器 fixture + 宿主 fixture` 的两段式会丢掉「容器先起来」的顺序保证（第一次实现就是这么错的，表现为 `did not have matching fixture data`）。

因此每个目标环境一个 fixture：起容器 → 建宿主 → 主动触发一次 `Factory.Services` 让迁移跑完 → 用例执行 → 反序释放。

### 3. 新用例必须自证"真的连到了目标形态"

```csharp
Assert.True(db.Database.IsMySql(), "容器化冒烟必须真的跑在 MySQL 上");
Assert.True(topology.HasDistributedBackplane, "配了 Redis 就必须真的启用分布式背板");
```

没有这两条，这套测试最容易退化成"配置看起来对、其实悄悄回落 SQLite / 进程内缓存"的自我安慰——比没有测试更危险。

### 4. 限流单独对着 Redis 验，不借道宿主

`DistributedRateLimiterTests` 直接构造 `DistributedWindowRateLimiter`，覆盖四件事：

| 用例 | 守的约束 |
| --- | --- |
| 超出额度即拒绝 + `RetryAfter` 在窗口内 | 客户端退避有依据，而不是瞎猜 |
| 计数键必须有 TTL | `INCR` + `EXPIRE` 拆两步时最易留下的永久键，会把一个 IP 永久封死 |
| 20 并发抢 5 个额度只放行 5 个 | 单条 Lua 脚本的原子性（拆成两条命令必然会超发） |
| Redis 不可达时回落本地窗口（2 放行 1 拒绝） | 依赖抖动既不能放行（等于绕过限流）也不能全拒 |

宿主的 `RateLimit:*PermitPerMinute=1000` 是给业务用例让路的，不能用来验证限流。

## 后果

**收益**

- MySQL / PostgreSQL 方言与迁移、Redis 背板、分布式限流的原子性与 TTL 进入可重复验证的范围。
- 默认路径零成本：`dotnet test` 当前 223 个用例里 212 通过 + 11 跳过，约 8 秒。
- 跳过数是可见指标：如果某天有人把门禁写错，摘要里的跳过数会立刻异常。

**代价**

- 开启门禁后需要 Docker，且首次要拉三个镜像（`mysql:8.4` / `postgres:17-alpine` / `redis:8.6`），本机约 1 分钟。
- 容器镜像版本现在有两处要对齐（AppHost 与测试 fixture）。刻意不抽成共享常量：测试项目不引用 AppHost（那是 `Exe` + Aspire SDK），抽常量的代价大于两行重复；改为在 AGENTS.md 里写明"改一处要改两处"。
- 仍未覆盖：前端 WASM、Aspire 编排本身（需 `Aspire.Hosting.Testing`）。
