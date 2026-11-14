using JasperFx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShortLink.Application.Abstractions;
using ShortLink.Application.Messaging;
using ShortLink.Infrastructure.Options;
using ShortLink.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Kafka;
using Wolverine.MySql;
using Wolverine.Postgresql;
using Wolverine.RabbitMQ;
using Wolverine.Sqlite;

namespace ShortLink.Infrastructure.Messaging;

/// <summary>
/// 消息总线装配。
/// <para>
/// MCP 与 HTTP 一样是入口，而这一层是「入口之后的解耦」：把点击落库、封顶审计这类副作用
/// 从请求路径上剥下来，换成可观测、可重试、可换传输的消息处理。
/// </para>
/// <para>
/// 三种组合，按部署形态选：
/// </para>
/// <list type="table">
/// <item>
/// <term>local + 不持久化（默认）</term>
/// <description>纯进程内。零外部依赖，行为最接近引入前的内存队列，进程崩溃丢队列内消息。</description>
/// </item>
/// <item>
/// <term>local + 持久化</term>
/// <description>消息先落 outbox 再本地消费。崩溃不丢，但写放大明显（每条消息多一次 INSERT/DELETE）。
/// 注意该模式下<b>点击也会持久化</b>，高频流量下不推荐。</description>
/// </item>
/// <item>
/// <term>kafka</term>
/// <description>跨实例消费、可供外部系统订阅、支持重放。需要真实 broker，
/// 启动期会校验 <c>Messaging:Kafka:BootstrapServers</c>。</description>
/// </item>
/// <item>
/// <term>rabbitmq</term>
/// <description>跨实例消费，多实例按队列分摊消息。需要真实 broker，
/// 启动期会校验 <c>Messaging:RabbitMq:ConnectionString</c>。</description>
/// </item>
/// </list>
/// </summary>
public static class MessagingSetup
{
    public static IServiceCollection AddShortLinkMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.SectionName));

        var options = configuration.GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()
                      ?? new MessagingOptions();

        // 配置错误在启动期暴露，不要等到第一次跳转才发现消息发不出去。
        options.Validate();

        var database = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>()
                       ?? new DatabaseOptions();

        services.AddWolverine(wolverine =>
        {
            // handler 与工具类都在 Infrastructure 程序集；显式指定避免扫描到测试宿主或 Api。
            wolverine.ApplicationAssembly = typeof(MessagingSetup).Assembly;
            wolverine.ServiceName = "shortlink";

            // 消息处理（点击批落库、封顶审计）默认不建 span，只有发送侧有痕迹，
            // 「这批点击为什么慢/失败」就断在 handler 里。Full 模式会补上接收与执行两段，
            // ActivitySource 名为 "Wolverine"，由 Api 侧的 tracing.AddSource 订阅。
            wolverine.InvokeTracing = InvokeTracingMode.Full;

            // Wolverine 6 起默认禁止生成代码里出现「服务定位」，它希望所有依赖都能被内联构造。
            // 但 EF Core 的 DbContext 是通过 AddDbContext 注册的：DbContextOptions 由不透明工厂委托产出，
            // 代码生成无法内联，只能运行时从容器取。于是对<b>这一个类型</b>精确放行——
            // 不要图省事改全局 ServiceLocationPolicy，那等于对所有 handler 关掉这项检查。
            wolverine.CodeGeneration.AlwaysUseServiceLocationFor<ShortLinkDbContext>();
            wolverine.CodeGeneration.AlwaysUseServiceLocationFor<IClickQuota>();

            // 点击：本地批处理。BatchSize 与 TriggerTime 共同决定「什么时候写一批」——
            // 满了立刻写，没满则等 TriggerTime，两者取先到者。
            wolverine.BatchMessagesOf<ClickRecorded>(batching =>
            {
                batching.BatchSize = Math.Max(options.ClickBatchSize, 1);
                batching.TriggerTime = TimeSpan.FromMilliseconds(Math.Max(options.ClickBatchTimeoutMilliseconds, 50));
            });

            if (options.Durable)
            {
                ConfigureDurablePersistence(wolverine, database);

                // 持久化的只有低频领域事件，点击除外——见方法内说明。
                ConfigureLocalQueueDurability(wolverine);
            }

            if (options.IsKafka)
            {
                // 有本地 handler，却要把消息发去 broker——这正是这个开关的用途：
                // 否则约定式路由会给 ClickRecorded 加一条本地订阅，消息既进 Kafka 又被本地消费，
                // 同一批点击被记两遍。
                wolverine.Policies.DisableConventionalLocalRouting();
                ConfigureKafka(wolverine, options);
            }
            else if (options.IsRabbitMq)
            {
                // 与 Kafka 同理：不关约定式路由，同一条消息会被本地队列与 RabbitMQ 各消费一次。
                wolverine.Policies.DisableConventionalLocalRouting();
                ConfigureRabbitMq(wolverine, options);
            }

            // ── 点击：永远只在内存里排队，不进消息表 ──
            // 点击是全系统唯一的高频消息（每次跳转一条）。一旦被 enrol 进消息表，
            // 每跳转一次就多一次 INSERT + 一次 DELETE，而换来的只是「进程崩溃不丢一条点击」——
            // 点击本来就可丢，这笔交换在任何量级下都不划算。
            // 注意：本行只在与「没有全局 durable 策略」配合时才算数；一旦有人调用
            // Policies.UseDurableLocalQueues()，启动期的端点策略会覆盖它。见 ConfigureLocalQueueDurability。
            wolverine.LocalQueueFor<ClickRecorded>().BufferedInMemory();
        });

        // IMessageBus 是 scoped，记录器必须同生命周期。
        services.AddScoped<IClickRecorder, WolverineClickRecorder>();

        return services;
    }

    /// <summary>
    /// 持久化消息存储。按 <c>Database:Provider</c> 选实现，与业务库同源，省一套运维。
    /// <para>
    /// 注意：连接串在这里读取（注册期），与业务 <c>DbContext</c> 的「运行时解析」不同——
    /// Wolverine 的持久化配置只接受构建期参数。测试宿主若要把业务库指到临时 SQLite，
    /// 需通过环境变量或 <c>UseSetting</c> 而非 <c>AddInMemoryCollection</c>，
    /// 否则这里读到的仍是 appsettings 的值。默认 <c>Durable=false</c> 时完全不触碰这条路径。
    /// </para>
    /// </summary>
    private static void ConfigureDurablePersistence(WolverineOptions wolverine, DatabaseOptions database)
    {
        var connectionString = database.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Messaging:Durable 为 true 时必须配置 Database:ConnectionString（消息存储复用业务库）");
        }

        switch (database.Provider?.Trim().ToLowerInvariant())
        {
            case "sqlite":
                wolverine.PersistMessagesWithSqlite(connectionString);
                break;

            case "postgresql":
            case "postgres":
                wolverine.PersistMessagesWithPostgresql(connectionString);
                break;

            default:
                wolverine.PersistMessagesWithMySql(connectionString);
                break;
        }

        // 本地队列本身<b>不</b>走 durable，见 ConfigureLocalQueueDurability 的说明。

        // 开箱即用：启动时自动补齐 outbox/inbox 表。
        // 生产环境若要求 schema 变更走审批，改成 AutoCreate.None 并用独占的迁移流程。
        wolverine.AutoBuildMessageStorageOnStartup = AutoCreate.CreateOrUpdate;
    }

    /// <summary>
    /// 本地队列的持久化范围：<b>只给低频领域事件</b>，点击永远留在内存。
    /// <para>
    /// 为什么不用 <c>Policies.UseDurableLocalQueues()</c>：它注册的是<b>端点策略</b>
    /// （<c>AllLocalQueues(q =&gt; q.UseDurableInbox())</c>），在宿主启动、端点构建时才应用，
    /// 一定晚于这里对单个队列的设置。所以只要调了它，后面对点击队列的 <c>BufferedInMemory()</c>
    /// 就会被覆盖——实测表现为每跳转一次就多一行 <c>wolverine_incoming_envelopes</c>，
    /// 点击这种高频数据在消息表里做 INSERT + DELETE，正是要避免的写放大。
    /// </para>
    /// <para>
    /// 反过来逐个 opt-in 才不会互相打架：这里没有全局策略，谁需要持久化谁自己声明。
    /// 新增会发布的消息类型时，默认就是内存传输，只有「丢了要人工补」的事件才加进来。
    /// </para>
    /// </summary>
    private static void ConfigureLocalQueueDurability(WolverineOptions wolverine)
    {
        // 封顶审计：低频（每条短链一生一次），丢了就少一条安全事件记录，值得进消息表。
        wolverine.LocalQueueFor<LinkClickCapped>().UseDurableInbox();
        wolverine.LocalQueueFor<LinkDisabled>().UseDurableInbox();
    }

    /// <summary>
    /// Kafka 模式：发出去的消息要能被同组实例收回来，否则「换成 Kafka」只是一个把消息
    /// 扔到黑洞里的开关——点击永远不会落库。
    /// <para>
    /// 三个 topic 用<b>一个</b>消费者收，减少分区重平衡与启动开销。
    /// 需要在多组消费者之间分流时，用 <c>Messaging:Kafka:ConsumerGroup</c> 区分。
    /// </para>
    /// </summary>
    private static void ConfigureKafka(WolverineOptions wolverine, MessagingOptions options)
    {
        wolverine.UseKafka(options.Kafka.BootstrapServers);

        // topic 名集中由 MessagingOptions.TopicFor 生成，避免各处拼出不一致的名字。
        var clickTopic = options.TopicFor(nameof(ClickRecorded));
        var cappedTopic = options.TopicFor(nameof(LinkClickCapped));
        var disabledTopic = options.TopicFor(nameof(LinkDisabled));

        wolverine.PublishMessage<ClickRecorded>().ToKafkaTopic(clickTopic);
        wolverine.PublishMessage<LinkClickCapped>().ToKafkaTopic(cappedTopic);
        wolverine.PublishMessage<LinkDisabled>().ToKafkaTopic(disabledTopic);

        wolverine.ListenToKafkaTopics(clickTopic, cappedTopic, disabledTopic);
    }

    /// <summary>
    /// RabbitMQ 模式：三种消息共用一个队列，多实例作为竞争消费者分摊。
    /// <para>
    /// 用 <c>AutoProvision</c> 自动声明队列与默认死信队列；生产若要求手工建队列，
    /// 预先建好同名队列后不会再被覆盖。这里不再叠加 <c>UseDurableInbox</c>：
    /// broker 侧「未 ack 即重新投递」已经覆盖了进程崩溃的语义，
    /// 再给共享队列（含高频点击）套一层 inbox 只会多一次 INSERT + DELETE。
    /// </para>
    /// <para>
    /// 需要按消息类型隔离吞吐时，把下面三行改成各自命名队列，并逐个 Listen 即可。
    /// </para>
    /// </summary>
    private static void ConfigureRabbitMq(WolverineOptions wolverine, MessagingOptions options)
    {
        var queue = options.RabbitMq.QueueName;

        wolverine.UseRabbitMq(options.RabbitMq.ConnectionString).AutoProvision();

        wolverine.PublishMessage<ClickRecorded>().ToRabbitQueue(queue);
        wolverine.PublishMessage<LinkClickCapped>().ToRabbitQueue(queue);
        wolverine.PublishMessage<LinkDisabled>().ToRabbitQueue(queue);

        wolverine.ListenToRabbitQueue(queue);
    }
}
