namespace ShortLink.Infrastructure.Options;

public sealed class KafkaTransportOptions
{
    /// <summary>broker 地址，如 <c>localhost:9092</c>；多个用逗号分隔。</summary>
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>topic 前缀。最终 topic 名为「前缀 + 消息类型名」，如 <c>shortlink.ClickRecorded</c>。</summary>
    public string TopicPrefix { get; set; } = "shortlink.";

    /// <summary>消费组。多实例同组会分摊分区，不同组各自收到全量。</summary>
    public string ConsumerGroup { get; set; } = "shortlink";
}
