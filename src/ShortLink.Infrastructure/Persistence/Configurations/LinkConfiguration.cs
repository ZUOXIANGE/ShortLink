using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShortLink.Domain.Links;

namespace ShortLink.Infrastructure.Persistence.Configurations;

public sealed class LinkConfiguration : IEntityTypeConfiguration<Link>
{
    public void Configure(EntityTypeBuilder<Link> builder)
    {
        builder.ToTable("short_links");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.WorkspaceId).HasColumnName("workspace_id").IsRequired();
        builder.Property(x => x.DomainId).HasColumnName("domain_id").IsRequired();
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(32).IsRequired();
        builder.Property(x => x.TargetUrl).HasColumnName("target_url").HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(200);
        builder.Property(x => x.Status).HasColumnName("status").HasConversion<int>().IsRequired();
        builder.Property(x => x.RedirectType).HasColumnName("redirect_type").HasConversion<int>().IsRequired();
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
        builder.Property(x => x.StartsAtUtc).HasColumnName("starts_at_utc");
        builder.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(256);
        builder.Property(x => x.Remark).HasColumnName("remark").HasMaxLength(500);
        builder.Property(x => x.ClickCount).HasColumnName("click_count").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
        builder.Property(x => x.LastAccessedAtUtc).HasColumnName("last_accessed_at_utc");
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(x => x.MaxClicks).HasColumnName("max_clicks");
        builder.Property(x => x.BotPolicy).HasColumnName("bot_policy").HasConversion<int>().IsRequired();
        builder.Property(x => x.IpAllowList).HasColumnName("ip_allow_list").HasMaxLength(2000);
        builder.Property(x => x.IpBlockList).HasColumnName("ip_block_list").HasMaxLength(2000);
        builder.Property(x => x.FallbackUrl).HasColumnName("fallback_url").HasMaxLength(2048);
        builder.Property(x => x.AntiBlockEnabled).HasColumnName("anti_block_enabled").IsRequired();
        builder.Property(x => x.ConversionSecret).HasColumnName("conversion_secret").HasMaxLength(128);
        builder.Property(x => x.ConcurrencyToken)
            .HasColumnName("concurrency_token")
            .HasMaxLength(32)
            .IsRequired()
            .IsConcurrencyToken();

        // 短码只在同一域名内唯一：s.example.com/abc 与 b.example.com/abc 可以共存。
        builder.HasIndex(x => new { x.DomainId, x.Code }).IsUnique().HasDatabaseName("ux_short_links_domain_code");
        builder.HasIndex(x => x.Code).HasDatabaseName("ix_short_links_code");
        builder.HasIndex(x => x.ExpiresAtUtc).HasDatabaseName("ix_short_links_expires_at");
        builder.HasIndex(x => x.ClickCount).HasDatabaseName("ix_short_links_click_count");
        // 列表默认按工作区 + 创建时间倒序，走这条复合索引避免全表扫描。
        builder.HasIndex(x => new { x.WorkspaceId, x.CreatedAtUtc })
            .HasDatabaseName("ix_short_links_workspace_created");
        builder.HasIndex(x => new { x.WorkspaceId, x.CreatedByUserId })
            .HasDatabaseName("ix_short_links_workspace_creator");
    }
}
