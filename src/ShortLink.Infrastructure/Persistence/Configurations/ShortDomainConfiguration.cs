using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShortLink.Domain.Domains;

namespace ShortLink.Infrastructure.Persistence.Configurations;

public sealed class ShortDomainConfiguration : IEntityTypeConfiguration<ShortDomain>
{
    public void Configure(EntityTypeBuilder<ShortDomain> builder)
    {
        builder.ToTable("short_domains");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(x => x.Host).HasColumnName("host").HasMaxLength(128).IsRequired();
        builder.Property(x => x.Scheme).HasColumnName("scheme").HasMaxLength(8).IsRequired();
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(64).IsRequired();
        builder.Property(x => x.IsDefault).HasColumnName("is_default").IsRequired();
        builder.Property(x => x.DefaultSlot).HasColumnName("default_slot");
        builder.Property(x => x.IsEnabled).HasColumnName("is_enabled").IsRequired();
        builder.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();

        builder.HasIndex(x => x.Host).IsUnique().HasDatabaseName("ux_short_domains_host");
        builder.HasIndex(x => x.DefaultSlot).IsUnique().HasDatabaseName("ux_short_domains_default_slot");
    }
}
