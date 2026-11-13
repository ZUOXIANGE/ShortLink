using Microsoft.Extensions.Configuration;
using ShortLink.Infrastructure.Options;
using Xunit;

namespace ShortLink.UnitTests;

/// <summary>
/// 消息配置的校验与命名。
/// <para>
/// 这里防的是「配错了却起得来」：消息配置的错误有个共同特点——启动期毫无异常，
/// 等到某次跳转或某次审计缺数据才暴露，而那时已经丢了不知道多少条。
/// 所以校验必须在 <c>Validate()</c> 里当场抛。
/// </para>
/// </summary>
public sealed class MessagingOptionsTests
{
    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Defaults_Should_Be_Local_And_Not_Durable()
    {
        var options = Build([]).GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()
                      ?? new MessagingOptions();

        // 默认值要能「裸奔部署」：不依赖 Redis/Kafka/消息表，一个 SQLite 文件就能跑起来。
        Assert.Equal(MessageTransports.Local, options.Transport);
        Assert.False(options.IsKafka);
        Assert.False(options.Durable);
        Assert.True(options.ClickBatchSize > 0);
        Assert.True(options.ClickBatchTimeoutMilliseconds > 0);

        options.Validate();
    }

    [Fact]
    public void Kafka_Without_BootstrapServers_Should_Throw_On_Validate()
    {
        var options = Build(new Dictionary<string, string?>
        {
            ["Messaging:Transport"] = "kafka",
        }).GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()!;

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("BootstrapServers", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_Transport_Should_Throw_On_Validate()
    {
        var options = Build(new Dictionary<string, string?>
        {
            ["Messaging:Transport"] = "rabbitmq",
        }).GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()!;

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Transport", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Kafka_With_Empty_Topic_Prefix_Should_Throw_On_Validate()
    {
        var options = Build(new Dictionary<string, string?>
        {
            ["Messaging:Transport"] = "kafka",
            ["Messaging:Kafka:BootstrapServers"] = "localhost:9092",
            ["Messaging:Kafka:TopicPrefix"] = "",
        }).GetSection(MessagingOptions.SectionName).Get<MessagingOptions>()!;

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("TopicPrefix", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Topic_Name_Should_Be_Prefix_Plus_Message_Type()
    {
        var options = new MessagingOptions
        {
            Transport = MessageTransports.Kafka,
            Kafka = new KafkaTransportOptions
            {
                BootstrapServers = "localhost:9092",
                TopicPrefix = "stage.",
            },
        };

        options.Validate();

        // topic 名只此一处生成：外部系统订阅时对不上的成本，比多一层间接高得多。
        Assert.Equal("stage.ClickRecorded", options.TopicFor("ClickRecorded"));
    }

    [Fact]
    public void Transport_Comparison_Should_Ignore_Case()
    {
        var options = new MessagingOptions { Transport = "KAFKA" };

        // 配置大小写不该是陷阱：环境变量注入时很难保证写成小写。
        Assert.True(options.IsKafka);
    }
}
