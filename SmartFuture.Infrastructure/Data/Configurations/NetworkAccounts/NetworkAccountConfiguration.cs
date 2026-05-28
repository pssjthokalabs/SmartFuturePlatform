using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.NetworkAccounts;

namespace SmartFuture.Infrastructure.Data.Configurations.NetworkAccounts;

public class NetworkAccountConfiguration : IEntityTypeConfiguration<NetworkAccount>
{
    public void Configure(EntityTypeBuilder<NetworkAccount> builder)
    {
        builder.ToTable("NetworkAccounts");

        builder.HasKey(n => n.Id);

        builder.Property(n => n.CreatedAtUtc).IsRequired();
        builder.Property(n => n.UpdatedAtUtc);
        builder.Property(n => n.RowVersion).IsRowVersion();

        builder.Property(n => n.AccountNumber).IsRequired().HasMaxLength(40);
        builder.Property(n => n.Username).IsRequired().HasMaxLength(120);
        builder.Property(n => n.PasswordHash).HasMaxLength(500);

        builder.Property(n => n.Status).HasConversion<int>().IsRequired();
        builder.Property(n => n.Source).HasConversion<int>().IsRequired();
        builder.Property(n => n.PackageType).HasConversion<int>().IsRequired();
        builder.Property(n => n.ProvisioningStatus).HasConversion<int>().IsRequired();
        builder.Property(n => n.ProvisioningAttemptCount).IsRequired();

        builder.Property(n => n.CurrentIpAddress).HasMaxLength(45);
        builder.Property(n => n.NasIdentifier).HasMaxLength(100);

        builder.Property(n => n.ProviderName).IsRequired().HasMaxLength(60);
        builder.Property(n => n.ProviderReference).HasMaxLength(200);

        builder.Property(n => n.PackageName).IsRequired().HasMaxLength(200);
        builder.Property(n => n.PackageSpeedLabel).HasMaxLength(100);
        builder.Property(n => n.PackagePrice).HasPrecision(18, 2);

        builder.Property(n => n.AdminNotes).HasMaxLength(2000);
        builder.Property(n => n.LastFailureReason).HasMaxLength(2000);
        builder.Property(n => n.SuspensionReason).HasMaxLength(2000);
        builder.Property(n => n.TerminationReason).HasMaxLength(2000);

        builder.HasOne(n => n.Order)
            .WithMany()
            .HasForeignKey(n => n.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(n => n.LastStatusChangedByUser)
            .WithMany()
            .HasForeignKey(n => n.LastStatusChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(n => n.RadiusProfile)
            .WithMany()
            .HasForeignKey(n => n.RadiusProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(n => n.ProvisioningStatus);
        builder.HasIndex(n => n.AccountNumber).IsUnique();
        builder.HasIndex(n => n.Username).IsUnique();
        builder.HasIndex(n => n.OrderId);
        builder.HasIndex(n => n.Status);
        builder.HasIndex(n => n.PackageType);
        builder.HasIndex(n => n.ProviderName);
        builder.HasIndex(n => n.ProviderReference);
        builder.HasIndex(n => n.CreatedAtUtc);
        builder.HasIndex(n => n.ProvisionedAtUtc);
        builder.HasIndex(n => n.TerminatedAtUtc);
    }
}
