using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class CustomerPaymentMandateConfiguration : IEntityTypeConfiguration<CustomerPaymentMandate>
{
    public void Configure(EntityTypeBuilder<CustomerPaymentMandate> builder)
    {
        builder.ToTable("CustomerPaymentMandates");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.CreatedAtUtc).IsRequired();
        builder.Property(m => m.UpdatedAtUtc);
        builder.Property(m => m.RowVersion).IsRowVersion();

        builder.Property(m => m.Provider).HasConversion<int>().IsRequired();
        builder.Property(m => m.ConsentSource).HasConversion<int>().IsRequired();

        builder.Property(m => m.ProviderCustomerCode).HasMaxLength(200);

        // Encrypted blob. Wide column to absorb future provider format
        // changes without another migration.
        builder.Property(m => m.AuthorizationCodeProtected)
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(m => m.AuthorizationSignature).HasMaxLength(200);
        builder.Property(m => m.Channel).HasMaxLength(40);
        builder.Property(m => m.CardType).HasMaxLength(40);
        builder.Property(m => m.Bank).HasMaxLength(120);
        builder.Property(m => m.Last4).HasMaxLength(4);
        builder.Property(m => m.ExpMonth).HasMaxLength(2);
        builder.Property(m => m.ExpYear).HasMaxLength(4);
        builder.Property(m => m.AccountName).HasMaxLength(200);
        builder.Property(m => m.CustomerEmail).HasMaxLength(320);

        builder.Property(m => m.MetadataJson).HasColumnType("nvarchar(max)");

        builder.HasOne(m => m.User)
            .WithMany()
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => m.UserId);
        builder.HasIndex(m => new { m.UserId, m.Provider });
        // Dedupe — same card per user/provider is upserted, not duplicated.
        builder.HasIndex(m => new { m.UserId, m.Provider, m.AuthorizationSignature })
            .HasFilter("[AuthorizationSignature] IS NOT NULL")
            .IsUnique();
        builder.HasIndex(m => m.IsActive);
        builder.HasIndex(m => m.IsDefault);
    }
}
