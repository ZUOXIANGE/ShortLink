namespace ShortLink.Infrastructure.Options;

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
