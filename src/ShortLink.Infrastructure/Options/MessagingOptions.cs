namespace ShortLink.Infrastructure.Options;

/// <summary>
/// 消息与最终一致性的配置。
/// <para>
/// 三个开关互相独立，别混为一谈：
/// </para>
/// <list type="bullet">
/// <item><see cref="Transport"/> 决定消息<b>发去哪</b>（进程内 / RabbitMQ / Kafka）。</item>
/// <item><see cref="Durable"/> 决定消息<b>是否先落库</b>（outbox）。</item>
/// <item><see cref="ClickBatchSize"/> 等决定点击消息<b>怎么聚合</b>。</item>
/// </list>
/// 默认全是最保守的组合：进程内、不落库、点按批聚合。这样一次部署不需要任何额外中间件。
/// </remarks>
public sealed class MessagingOptions
{
    public const string SectionName = "Messaging";

    /// <summary>传输方式：<c>local</c>（默认，进程内）、<c>rabbitmq</c> 或 <c>kafka</c>。</summary>
    public string Transport { get; set; } = MessageTransports.Local;

    /// <summary>
    /// 是否启用本地消息表（outbox/inbox）。
    /// <para>
    /// 开启后低频领域事件先落库再投递，进程崩溃不丢，代价是每条消息多一次 INSERT + DELETE。
    /// <b>这个开关不覆盖点击</b>：<c>ClickRecorded</c> 的队列被固定在内存（<c>BufferedInMemory</c>），
    /// 与这里取什么值无关——给每秒上千条的点击做 inbox，写放大远大于「丢一条」的损失。
    /// 持久化范围是逐个队列 opt-in 的，见 <c>MessagingSetup.ConfigureLocalQueueDurability</c>。
    /// </para>
    /// </summary>
    public bool Durable { get; set; }

    /// <summary>点击消息的单批条数。过小则事务频繁，过大则单次持锁时间长。</summary>
    public int ClickBatchSize { get; set; } = 500;

    /// <summary>点击消息的聚合等待时长（毫秒）。把突发流量合并成一批，代价是统计延迟。</summary>
    public int ClickBatchTimeoutMilliseconds { get; set; } = 500;

    /// <summary>RabbitMQ 连接参数，仅在 <see cref="Transport"/> 为 <c>rabbitmq</c> 时生效。</summary>
    public RabbitMqTransportOptions RabbitMq { get; set; } = new();

    /// <summary>Kafka 连接参数，仅在 <see cref="Transport"/> 为 <c>kafka</c> 时生效。</summary>
    public KafkaTransportOptions Kafka { get; set; } = new();

    public bool IsRabbitMq => string.Equals(Transport, MessageTransports.RabbitMq, StringComparison.OrdinalIgnoreCase);

    public bool IsKafka => string.Equals(Transport, MessageTransports.Kafka, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 启动期校验。宁可起不来也不要在运行中静默丢消息——
    /// 配了 RabbitMQ 却没填连接串、配了 Kafka 却没填 broker 地址，等到第一次跳转才发现就太晚了。
    /// </summary>
    public void Validate()
    {
        if (string.Equals(Transport, MessageTransports.Local, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (IsRabbitMq)
        {
            if (string.IsNullOrWhiteSpace(RabbitMq.ConnectionString))
            {
                throw new InvalidOperationException(
                    "Messaging:Transport 为 rabbitmq 时必须配置 Messaging:RabbitMq:ConnectionString");
            }

            if (string.IsNullOrWhiteSpace(RabbitMq.QueueName))
            {
                throw new InvalidOperationException("Messaging:RabbitMq:QueueName 不能为空");
            }

            return;
        }

        if (!IsKafka)
        {
            throw new InvalidOperationException(
                $"Messaging:Transport 只能是 'local'、'rabbitmq' 或 'kafka'，当前为 '{Transport}'");
        }

        if (string.IsNullOrWhiteSpace(Kafka.BootstrapServers))
        {
            throw new InvalidOperationException(
                "Messaging:Transport 为 kafka 时必须配置 Messaging:Kafka:BootstrapServers");
        }

        if (string.IsNullOrWhiteSpace(Kafka.TopicPrefix))
        {
            throw new InvalidOperationException("Messaging:Kafka:TopicPrefix 不能为空");
        }
    }

    /// <summary>把消息类型映射成 topic 名。集中一处，避免各处拼字符串拼出不一致的名字。</summary>
    public string TopicFor(string messageName) => $"{Kafka.TopicPrefix}{messageName}";
}

/// <summary>传输方式码值。</summary>
public static class MessageTransports
{
    /// <summary>进程内队列。单实例部署、或不需要跨实例消费时使用。</summary>
    public const string Local = "local";

    /// <summary>RabbitMQ。需要跨实例消费、但不要求重放或超大规模吞吐时使用。</summary>
    public const string RabbitMq = "rabbitmq";

    /// <summary>Kafka。跨实例消费、需要外部系统订阅或需要重放时使用。</summary>
    public const string Kafka = "kafka";
}

public sealed class RabbitMqTransportOptions
{
    /// <summary>AMQP 连接串，如 <c>amqp://user:password@localhost:5672</c>。</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// 收发共用的队列名。三种消息走同一个队列，理由与 Kafka 侧「一个消费者收全量」相同：
    /// 减少连接与声明开销。多环境共用一个 broker 时必须改成互不相同的名字，否则会互相抢消息。
    /// </summary>
    public string QueueName { get; set; } = "shortlink";
}

public sealed class KafkaTransportOptions
{
    /// <summary>broker 地址，如 <c>localhost:9092</c>；多个用逗号分隔。</summary>
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>topic 前缀。最终 topic 名为「前缀 + 消息类型名」，如 <c>shortlink.ClickRecorded</c>。</summary>
    public string TopicPrefix { get; set; } = "shortlink.";

    /// <summary>消费组。多实例同组会分摊分区，不同组各自收到全量。</summary>
    public string ConsumerGroup { get; set; } = "shortlink";
}
