namespace ShortLink.Infrastructure.Options;

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
