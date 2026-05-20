using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Identity;

namespace SmartFuture.Infrastructure.Data.Configurations.Identity;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.FirstName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(u => u.LastName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(u => u.AccountStatus)
            .HasConversion<int>()
            .IsRequired();

        builder.Property(u => u.IsActive)
            .IsRequired();

        builder.Property(u => u.CreatedAtUtc)
            .IsRequired();

        builder.Property(u => u.UpdatedAtUtc);

        builder.HasIndex(u => u.AccountStatus);
        builder.HasIndex(u => u.IsActive);

        // Phase 41 — friendly user number. Unique across the table but
        // filtered so the index allows NULL rows during the brief
        // backfill window between migration and DbInitializer running.
        builder.Property(u => u.UserNumber);
        builder.HasIndex(u => u.UserNumber)
            .IsUnique()
            .HasFilter("[UserNumber] IS NOT NULL");
    }
}
