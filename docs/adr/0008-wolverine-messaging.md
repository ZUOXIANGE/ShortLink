# ADR 0008：用 Wolverine 承载点击落库与封顶审计

- 状态：已采纳
- 日期：2026-11-11
- 相关：`src/ShortLink.Infrastructure/Messaging/`、`src/ShortLink.Application/Messaging/MessagingContracts.cs`、`src/ShortLink.Infrastructure/Options/MessagingOptions.cs`

## 背景

引入前，项目里唯一一处「异步」是 `Analytics/ClickEventQueue` + `ClickEventFlushService`：一个进程内 `Channel<ClickEvent>` 加一个后台批处理循环。

它解决了「跳转不能被落库拖慢」，但留下了三个问题：

1. **不可替换**。想换成 Kafka 只有一条路——重写这个类。
2. **不可观测**。队列深度、失败、丢弃全靠自己埋点，没有重试与死信的概念。
3. **业务代码渗入管道**。封顶判定写在批处理循环里，与「怎么传消息」混在一处，改传输就得改业务。

判断标准很简单：**这已经是消息队列的职责了，只是实现得比较临时**。所以不修补，换实现。

## 决策

引入 `WolverineFx`，把「点击落库」与「封顶写审计」两条副作用搬到消息总线上。三个维度**互相独立**，不要混为一谈：

| 维度 | 配置项 | 决定什么 | 默认 |
| --- | --- | --- | --- |
| 传输 | `Messaging:Transport` | 消息**发去哪**（进程内 / Kafka） | `local` |
| 持久化 | `Messaging:Durable` | 是否先落 outbox 再投递 | `false` |
| 聚合 | `Messaging:ClickBatchSize` / `ClickBatchTimeoutMilliseconds` | 点击**怎么攒批** | 500 条 / 500ms |

默认组合是「裸奔部署」：一个 SQLite 文件就能跑，不依赖 Redis、Kafka 或消息表。

### 1. 点击也走总线，不做例外

点击是最高频的消息（每次跳转一条），最容易以「性能」为理由排除在总线之外。没有这么做，理由有三：

- **排除它就等于保留两套异步机制**。两套机制意味着两套失败语义、两套观测、两套关闭顺序——这比总线本身的开销贵得多。
- 开销是可测的：`PublishAsync` 在 local + 非持久化模式下只是入内存队列，实测跳转 P99 未变差（落库仍在批处理线程）。
- 真正贵的是**持久化**，不是总线。所以把「点击不持久化」写进配置语义（见第 3 条），而不是把点击排除在总线之外。

批处理参数：满 500 条立刻写，未满则等 500ms。两者取先到者，因此统计延迟上限是 500ms 量级——这与引入前的内存队列一致，不算退步。

### 2. 只在 Kafka 模式下关闭约定式路由

这是实现时踩的第一个坑，且**故障形态极安静**：

```
[INF] No routes can be determined for Envelope #08df... (ShortLink.Application.Messaging.ClickRecorded)
```

`PublishAsync` 正常返回、不抛异常、不进死信，消息就是消失了。因为 `Policies.DisableConventionalLocalRouting()` 关掉了「有本地 handler ⇒ 自动路由到本地队列」的约定，而当时并没有为任何消息类型显式声明目的地。

结论与取舍：

- **local 模式保留约定式路由**。约定在这里是准确的：消息类型 → 同名本地队列，与 `BatchMessagesOf` 给批处理器分配的「元素类型队列」是同一个，发布端与消费端不会错位。试图改成逐个 `LocalQueueFor<T>()` 只会得到一个名字对不上的孤儿队列。
- **Kafka 模式显式关闭它**。这正是该开关的语义：*有本地 handler，但消息要走 broker*。不关的话，`ClickRecorded` 会同时被本地队列和 Kafka 消费，同一批点击被记两遍——一个只在生产流量下才显现的双写。

Kafka 侧同时补了 `ListenToKafkaTopic`：只发不收等于「把消息扔进黑洞，点击永远不落库」。三个 topic 共用一个消费者，减少分区重平衡。

### 3. 消息表只装低频事件，点击永远在内存里

`Messaging:Durable` 决定「要不要有本地消息表（outbox/inbox）」，但它**不覆盖点击**。持久化范围是逐个队列 opt-in 的：

| 消息 | 队列模式 | 理由 |
| --- | --- | --- |
| `ClickRecorded` | 内存（`BufferedInMemory`） | 每次跳转一条，最高频；丢一条无感 |
| `LinkClickCapped` | durable（`UseDurableInbox`） | 每条短链一生一次；丢了就少一条安全事件 |
| `LinkDisabled` | durable | 同上 |

对点击来说，「先写消息表再消费」的收益是「进程崩溃不丢一条点击」，代价是每跳转一次多一次 `INSERT` + 一次 `DELETE`。这笔交换在任何量级下都不划算，所以在跳转热路径上必须彻底移除。

**这里有一个不易察觉的坑**（实现时先写错、再实测抓出来的）：

```csharp
wolverine.Policies.UseDurableLocalQueues();          // ✗ 不要用
wolverine.LocalQueueFor<ClickRecorded>().BufferedInMemory();  // 会被上面那行覆盖
```

`UseDurableLocalQueues()` 内部是 `AllLocalQueues(q => q.UseDurableInbox())`，注册的是一个 **`LambdaEndpointPolicy`**——在宿主启动、端点真正材料化时才应用。它与单队列 `BufferedInMemory()` 谁生效取决于队列实例被创建的时机，**不要依赖这种时序**：踩坑时两者同时存在，结果是点击照样进了消息表。

实测证据（`Messaging:Durable=true` + SQLite，点击 6 次）：

```
修复前  wolverine_incoming_envelopes: ClickRecorded × 6
修复后  wolverine_incoming_envelopes: LinkClickCapped × 1，ClickRecorded × 0
        click_events = 6，security_events = 1，links.click_count 正确
```

也就是说：**正确的做法是默认全部不持久化，只给需要的事件 opt-in**，而不是「全量 durable 再把点击摘出来」——后者摘不掉。

**这个不变式没有自动化测试守着，这是已知缺口。** 试过两种写法，都失败：

1. 读端点上的 `Mode`（在最小 `Host` 里装配消息层后断言 `BufferedInMemory`）。**在 bug 存在时依然全绿**——端点对象的模式与消息最终写进哪张表不是一回事。
2. 数消息表行数（同一最小宿主，派发一条点击后断言 0 行）。**同样在 bug 存在时通过**——最小宿主没有复现完整宿主的端点材料化路径，点击根本没被派发到目标队列。

一个在 bug 存在时仍然通过的测试，比没有测试更糟：它制造了已覆盖的假象。所以两条都被删掉了，宁可留一个诚实的缺口。另外，完整宿主也不便复制——同一进程里第二个 `WebApplicationFactory` 会因 Serilog 静态 logger 已冻结而抛异常。

结论：改动消息装配后，**必须手工重跑上面那个实验**（起 `Messaging__Durable=true` 的宿主 → 点几次 → 查 `wolverine_incoming_envelopes` 的 `message_type` 分布）。

### 4. 封顶审计：事务外发布，失败不回滚

`ClickRecordedHandler` 的事务边界是刻意设计的：

1. 按短链分组做一次 `ExecuteUpdateAsync` 增量累加（避免读-改-写丢更新）；
2. **累加之后**再判封顶——拿旧值判会漏停用；
3. 批量 `INSERT` 明细；
4. 提交之后才发 `LinkClickCapped` 并失效缓存。

第 4 步在事务外，是因为「已通知但实际没停用」比「停用了但没通知」更难收拾。审计消息发布失败只记日志，不回滚已提交的点击。

同理，`ClickRecordedHandler` 捕获异常后**不重新抛出**：点击是可丢数据，让总线对一批注定失败的写入无限重试，只会堵住后面所有消息。失败计入 `ClickPersistFailures` 指标。

### 5. 代码生成：两处必须显式配置

Wolverine 6 在代码生成上有两个会让人卡很久的点，都写进 `MessagingSetup` 的注释：

**其一，`WolverineFx.RuntimeCompilation` 是独立包。** 6.x 核心包不再内置 Roslyn 编译器，`TypeLoadMode.Dynamic`（开发环境的默认值）会直接启动失败：

```
TypeLoadMode.Dynamic ... no IAssemblyGenerator
```

**其二，`ServiceLocationPolicy.NotAllowed` 是 6.0 的新默认值。** 我们的 handler 注入 `ShortLinkDbContext`，而它由 `AddDbContext` 注册——`DbContextOptions<T>` 是「不透明 lambda 工厂」，代码生成无法内联构造，只能运行时从容器取。于是启动期抛：

```
InvalidServiceLocationException: Found service locations while generating code
for Message Handler for ClickRecorded[], but ServiceLocationPolicy.NotAllowed is in effect
```

修法不是把全局策略改成 `AllowedButWarn`（那等于对所有 handler 关掉这项检查），而是对这个类型**精确放行**：

```csharp
wolverine.CodeGeneration.AlwaysUseServiceLocationFor<ShortLinkDbContext>();
```

### 6. 测试策略：Local 全覆盖，Kafka 只保证配置正确

按部署形态排序，Kafka 是可选路径。因此：

- **local 路径全覆盖**，含突发点击不丢、封顶后停用、审计落库。
- **Kafka 路径不做 Testcontainers**。拉起 broker 会让整个测试套件从秒级变成分钟级，而它验证的东西（序列化、分区、消费组）恰恰是 Confluent 客户端与 Wolverine 自己的责任，不是本项目的行为。配置错误（缺 broker 地址、空 topic 前缀）由 `MessagingOptions.Validate()` 在启动期抛异常覆盖，这部分有单测。

**顺带修掉一个测试反模式**：原来验证异步副作用靠 `await Task.Delay(3_000)`。这个写法有两个方向的错——写短了偶发失败，写长了拖慢套件，而失败信息是「Expected Gone, Actual Found」，完全指不出是超时还是逻辑错。

现在统一换成 `WaitUntilAsync(condition, because)`：轮询到条件成立即继续，超时则带着 `because` 文案 `Assert.Fail`。本轮排查中，正是这种「等固定时长」的写法把 `No routes` 与 `InvalidServiceLocation` 两个启动期故障伪装成了业务断言失败。

## 后果

- 依赖新增 7 个包（`WolverineFx` 及 `.EntityFrameworkCore` / `.Sqlite` / `.MySql` / `.Postgresql` / `.Kafka` / `.RuntimeCompilation`，均 **6.64.0**）。全部集中在 `Directory.Packages.props` 的「消息与最终一致性」组。
- `ClickEventQueue` / `ClickEventFlushService` 删除，`IClickRecorder.Enqueue` 改为 `ValueTask EnqueueAsync(...)`（发布是异步的）。
- 启动期多一次代码生成（Dynamic 模式），开发环境首启约 +0.5s；生产可切 `Static` 预生成。
- 消息存储复用业务库（按 `Database:Provider` 选 Sqlite / MySql / PostgreSql），`Durable=false` 时完全不触碰；`Durable=true` 时也只承载 `LinkClickCapped` / `LinkDisabled` 这类低频事件，表大小与点击量无关。
- 未验证：真实 Kafka broker 下的端到端消费；`Durable=true` 在 MySql / PostgreSql 下的表结构（SQLite 路径已实测，靠 `AutoBuildMessageStorageOnStartup = CreateOrUpdate` 自动建表，生产若要求 schema 变更走审批需改）。
- 新增 `Messaging` 配置段，但**旧配置无需迁移**——所有键都有与引入前等价的默认值。
