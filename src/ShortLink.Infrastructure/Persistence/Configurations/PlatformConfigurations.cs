using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShortLink.Domain.Analytics;
using ShortLink.Domain.Links;
using ShortLink.Domain.Security;
using ShortLink.Domain.Workspaces;

namespace ShortLink.Infrastructure.Persistence.Configurations;

public sealed class LinkRouteConfiguration : IEntityTypeConfiguration<LinkRoute>
{
    public void Configure(EntityTypeBuilder<LinkRoute> builder)
    {
        builder.ToTable("link_routes");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.LinkId).HasColumnName("link_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Priority).HasColumnName("priority").IsRequired();
        builder.Property(x => x.Weight).HasColumnName("weight").IsRequired();
        builder.Property(x => x.TargetUrl).HasColumnName("target_url").HasMaxLength(2048).IsRequired();

        // 条件以 JSON 文本存储：条件结构随业务演进，宽表会不断加列且大部分行为空。
        builder.Property(x => x.ConditionsJson).HasColumnName("conditions_json").HasMaxLength(4000).IsRequired();

        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled").IsRequired();
        builder.Property(x => x.ClickCount).HasColumnName("click_count").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();

        // 短链删除时级联清理分支，避免孤儿规则继续参与路由。
        builder.HasOne<Link>()
            .WithMany()
            .HasForeignKey(x => x.LinkId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.LinkId, x.Priority }).HasDatabaseName("ix_link_routes_link_priority");
        builder.HasIndex(x => new { x.LinkId, x.Name })
            .IsUnique()
            .HasDatabaseName("ux_link_routes_link_name");
    }
}

public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("security_events");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.WorkspaceId).HasColumnName("workspace_id").IsRequired();
        builder.Property(x => x.LinkId).HasColumnName("link_id");
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32);
        builder.Property(x => x.Type).HasColumnName("type").HasConversion<int>().IsRequired();
        builder.Property(x => x.Message).HasColumnName("message").HasMaxLength(500).IsRequired();
        builder.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
        builder.Property(x => x.Country).HasColumnName("country").HasMaxLength(64);
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.HasIndex(x => new { x.WorkspaceId, x.CreatedAtUtc })
            .HasDatabaseName("ix_security_events_workspace_created");
        builder.HasIndex(x => new { x.LinkId, x.CreatedAtUtc })
            .HasDatabaseName("ix_security_events_link_created");
    }
}

public sealed class ConversionEventConfiguration : IEntityTypeConfiguration<ConversionEvent>
{
    public void Configure(EntityTypeBuilder<ConversionEvent> builder)
    {
        builder.ToTable("conversion_events");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.WorkspaceId).HasColumnName("workspace_id").IsRequired();
        builder.Property(x => x.LinkId).HasColumnName("link_id").IsRequired();
        builder.Property(x => x.RouteId).HasColumnName("route_id");
        builder.Property(x => x.EventId).HasColumnName("event_id").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Metric).HasColumnName("metric").HasMaxLength(64).IsRequired();

        // 金额保留 4 位小数：不同币种的最小单位差异较大，用 decimal 而非浮点避免累加误差。
        builder.Property(x => x.Value).HasColumnName("value").HasPrecision(18, 4).IsRequired();

        builder.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(64);
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        // 幂等键：同一短链下同一 event_id 只允许一条。
        builder.HasIndex(x => new { x.LinkId, x.EventId })
            .IsUnique()
            .HasDatabaseName("ux_conversion_events_link_event");
        builder.HasIndex(x => new { x.WorkspaceId, x.CreatedAtUtc })
            .HasDatabaseName("ix_conversion_events_workspace_created");
    }
}

public sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.ToTable("workspaces");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(64).IsRequired();
        builder.Property(x => x.IsDefault).HasColumnName("is_default").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("ux_workspaces_slug");
    }
}

public sealed class WorkspaceMemberConfiguration : IEntityTypeConfiguration<WorkspaceMember>
{
    public void Configure(EntityTypeBuilder<WorkspaceMember> builder)
    {
        builder.ToTable("workspace_members");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.WorkspaceId).HasColumnName("workspace_id").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.Role).HasColumnName("role").HasConversion<int>().IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.HasIndex(x => new { x.WorkspaceId, x.UserId })
            .IsUnique()
            .HasDatabaseName("ux_workspace_members_workspace_user");
    }
}
