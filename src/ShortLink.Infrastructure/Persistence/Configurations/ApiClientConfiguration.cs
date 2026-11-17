using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShortLink.Domain.ApiClients;

namespace ShortLink.Infrastructure.Persistence.Configurations;

public sealed class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> builder)
    {
        builder.ToTable("api_clients");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.WorkspaceId).HasColumnName("workspace_id").IsRequired();
        builder.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        builder.Property(x => x.KeyId).HasColumnName("key_id").HasMaxLength(48).IsRequired();
        builder.Property(x => x.SecretCipher).HasColumnName("secret_cipher").HasMaxLength(1024).IsRequired();
        builder.Property(x => x.SecretHint).HasColumnName("secret_hint").HasMaxLength(8).IsRequired();
        builder.Property(x => x.Role).HasColumnName("role").HasConversion<int>().IsRequired();
        builder.Property(x => x.Scopes).HasColumnName("scopes").HasMaxLength(512).IsRequired();
        builder.Property(x => x.RateLimitPerMinute).HasColumnName("rate_limit_per_minute");
        builder.Property(x => x.AllowedIps).HasColumnName("allowed_ips").HasMaxLength(1024);
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled").IsRequired();
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc");
        builder.Property(x => x.LastUsedAtUtc).HasColumnName("last_used_at_utc");
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
        builder.Property(x => x.RevokedAtUtc).HasColumnName("revoked_at_utc");

        // keyId 是认证入口的唯一查找键，必须唯一且区分大小写（MySQL 默认排序规则不区分）。
        builder.HasIndex(x => x.KeyId).IsUnique().HasDatabaseName("ux_api_clients_key_id");
        builder.HasIndex(x => new { x.WorkspaceId, x.IsEnabled }).HasDatabaseName("ix_api_clients_workspace_enabled");
        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_api_clients_user");
    }
}
