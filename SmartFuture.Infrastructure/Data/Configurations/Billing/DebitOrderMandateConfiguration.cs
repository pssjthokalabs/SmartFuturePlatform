using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class DebitOrderMandateConfiguration : IEntityTypeConfiguration<DebitOrderMandate>
{
    public void Configure(EntityTypeBuilder<DebitOrderMandate> builder)
    {
        builder.ToTable("DebitOrderMandates");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.CreatedAtUtc).IsRequired();
        builder.Property(d => d.UpdatedAtUtc);
        builder.Property(d => d.RowVersion).IsRowVersion();

        builder.Property(d => d.UserId).IsRequired();

        builder.Property(d => d.Status).HasConversion<int>().IsRequired();
        builder.Property(d => d.Frequency).HasConversion<int>().IsRequired();

        builder.Property(d => d.Amount).HasPrecision(18, 2);
        builder.Property(d => d.CurrencyCode).IsRequired().HasMaxLength(3);

        builder.Property(d => d.AccountHolderName).HasMaxLength(200);
        builder.Property(d => d.BankName).HasMaxLength(150);
        builder.Property(d => d.BankAccountLast4).HasMaxLength(4);
        builder.Property(d => d.BankAccountType).HasMaxLength(50);
        builder.Property(d => d.MaskedAccountReference).HasMaxLength(100);
        builder.Property(d => d.GatewayMandateReference).HasMaxLength(200);
        builder.Property(d => d.ExternalReference).HasMaxLength(100);

        builder.Property(d => d.Notes).HasMaxLength(2000);
        builder.Property(d => d.AdminNotes).HasMaxLength(3000);

        builder.HasOne(d => d.User)
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.CustomerProfile)
            .WithMany()
            .HasForeignKey(d => d.CustomerProfileId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.Order)
            .WithMany()
            .HasForeignKey(d => d.OrderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(d => d.LastStatusChangedByUser)
            .WithMany()
            .HasForeignKey(d => d.LastStatusChangedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => d.UserId);
        builder.HasIndex(d => d.CustomerProfileId);
        builder.HasIndex(d => d.OrderId);
        builder.HasIndex(d => d.Status);
        builder.HasIndex(d => d.Frequency);
        builder.HasIndex(d => d.PreferredDebitDay);
        builder.HasIndex(d => d.StartDateUtc);
        builder.HasIndex(d => d.EndDateUtc);
        builder.HasIndex(d => d.CreatedAtUtc);
        builder.HasIndex(d => d.GatewayMandateReference);

        builder.HasIndex(d => d.ExternalReference)
            .IsUnique()
            .HasFilter("[ExternalReference] IS NOT NULL");
    }
}
