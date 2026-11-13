namespace ShortLink.Infrastructure.Persistence;

/// <summary>点击清理的数据库租约与最后成功日期；由所有 API 副本共享，不依赖进程内状态。</summary>
public sealed class ClickRetentionLease
{
    public string Name { get; private set; } = string.Empty;

    public string? OwnerId { get; private set; }

    public DateTime LeaseUntilUtc { get; private set; }

    public DateOnly? LastCompletedDate { get; private set; }

    public static ClickRetentionLease Create(string name, string ownerId, DateTime leaseUntilUtc)
        => new() { Name = name, OwnerId = ownerId, LeaseUntilUtc = leaseUntilUtc };
}
