using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShortLink.Domain.Analytics;

namespace ShortLink.Infrastructure.Persistence.Configurations;

public sealed class ClickEventConfiguration : IEntityTypeConfiguration<ClickEvent>
{
    public void Configure(EntityTypeBuilder<ClickEvent> builder)
    {
        builder.ToTable("click_events");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.EventId).HasColumnName("event_id").IsRequired();
        builder.Property(x => x.WorkspaceId).HasColumnName("workspace_id").IsRequired();
        builder.Property(x => x.LinkId).HasColumnName("link_id").IsRequired();
        builder.Property(x => x.DomainId).HasColumnName("domain_id").IsRequired();
        builder.Property(x => x.RouteId).HasColumnName("route_id");
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32).IsRequired();
        builder.Property(x => x.ClickedAtUtc).HasColumnName("clicked_at_utc").IsRequired();
        builder.Property(x => x.ClickDate).HasColumnName("click_date").IsRequired();
        builder.Property(x => x.VisitorHash).HasColumnName("visitor_hash").HasMaxLength(64).IsRequired();
        builder.Property(x => x.Ip).HasColumnName("ip").HasMaxLength(64);
        builder.Property(x => x.UserAgent).HasColumnName("user_agent").HasMaxLength(512);
        builder.Property(x => x.Referer).HasColumnName("referer").HasMaxLength(1024);
        builder.Property(x => x.Country).HasColumnName("country").HasMaxLength(64);
        builder.Property(x => x.Province).HasColumnName("province").HasMaxLength(64);
        builder.Property(x => x.Device).HasColumnName("device").HasMaxLength(32);
        builder.Property(x => x.Browser).HasColumnName("browser").HasMaxLength(64);
        builder.Property(x => x.OperatingSystem).HasColumnName("operating_system").HasMaxLength(64);
        builder.Property(x => x.IsBot).HasColumnName("is_bot").IsRequired();

        builder.HasIndex(x => x.EventId).IsUnique().HasDatabaseName("ux_click_events_event_id");
        builder.HasIndex(x => new { x.ClickedAtUtc, x.Id }).HasDatabaseName("ix_click_events_clicked_id");
        builder.HasIndex(x => new { x.LinkId, x.Id }).HasDatabaseName("ix_click_events_link_id");
        builder.HasIndex(x => new { x.LinkId, x.ClickDate }).HasDatabaseName("ix_click_events_link_date");
        builder.HasIndex(x => x.ClickDate).HasDatabaseName("ix_click_events_click_date");
        builder.HasIndex(x => new { x.ClickDate, x.VisitorHash }).HasDatabaseName("ix_click_events_date_visitor");
        // A/B 分支统计：按短链 + 分支聚合，避免把明细拉进内存。
        builder.HasIndex(x => new { x.LinkId, x.RouteId }).HasDatabaseName("ix_click_events_link_route");
        builder.HasIndex(x => new { x.WorkspaceId, x.ClickDate }).HasDatabaseName("ix_click_events_workspace_date");
    }
}

public sealed class ClickRetentionLeaseConfiguration : IEntityTypeConfiguration<ClickRetentionLease>
{
    public void Configure(EntityTypeBuilder<ClickRetentionLease> builder)
    {
        builder.ToTable("click_retention_leases");
        builder.HasKey(x => x.Name);
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(64);
        builder.Property(x => x.OwnerId).HasColumnName("owner_id").HasMaxLength(32);
        builder.Property(x => x.LeaseUntilUtc).HasColumnName("lease_until_utc").IsRequired();
        builder.Property(x => x.LastCompletedDate).HasColumnName("last_completed_date");
    }
}
