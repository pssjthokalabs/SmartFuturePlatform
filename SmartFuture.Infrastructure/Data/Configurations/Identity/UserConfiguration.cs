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

        // Phase 43 — canonical phone form for duplicate detection.
        // Unique-filtered so legacy rows without a phone number can
        // coexist; backfill runs at startup, and any unresolvable
        // duplicates in legacy data are left null (with a warning) so
        // the constraint isn't violated.
        builder.Property(u => u.PhoneNumberNormalized)
            .HasMaxLength(32);
        builder.HasIndex(u => u.PhoneNumberNormalized)
            .IsUnique()
            .HasFilter("[PhoneNumberNormalized] IS NOT NULL");
    }
}
