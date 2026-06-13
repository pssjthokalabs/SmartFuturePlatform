using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Billing;

namespace SmartFuture.Infrastructure.Data.Configurations.Billing;

public class ServiceBillingScheduleConfiguration : IEntityTypeConfiguration<ServiceBillingSchedule>
{
    public void Configure(EntityTypeBuilder<ServiceBillingSchedule> builder)
    {
        builder.ToTable("ServiceBillingSchedules");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.CreatedAtUtc).IsRequired();
        builder.Property(s => s.UpdatedAtUtc);
        builder.Property(s => s.RowVersion).IsRowVersion();

        builder.Property(s => s.BillingCycle).HasConversion<int>().IsRequired();
        builder.Property(s => s.Status).HasConversion<int>().IsRequired();

        builder.Property(s => s.Amount).HasPrecision(18, 2).IsRequired();
        builder.Property(s => s.CurrencyCode).IsRequired().HasMaxLength(3);

        builder.Property(s => s.AnchorDayOfMonth).IsRequired();
        builder.Property(s => s.IsAutoBillable).IsRequired();

        builder.Property(s => s.Notes).HasMaxLength(2000);

        builder.HasOne(s => s.NetworkAccount)
            .WithMany()
            .HasForeignKey(s => s.NetworkAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        // Selection query the future generator needs: "active schedules
        // due for invoicing soon" — covered by (Status, NextInvoiceDateUtc).
        builder.HasIndex(s => new { s.Status, s.NextInvoiceDateUtc });
        builder.HasIndex(s => s.NetworkAccountId);
        builder.HasIndex(s => s.OrderId);
        builder.HasIndex(s => s.UserId);
    }
}
