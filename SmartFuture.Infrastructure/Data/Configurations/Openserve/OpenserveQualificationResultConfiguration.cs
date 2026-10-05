using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Openserve;

namespace SmartFuture.Infrastructure.Data.Configurations.Openserve;

public class OpenserveQualificationResultConfiguration : IEntityTypeConfiguration<OpenserveQualificationResult>
{
    public void Configure(EntityTypeBuilder<OpenserveQualificationResult> builder)
    {
        builder.ToTable("OpenserveQualificationResults");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc);
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.Property(r => r.Purpose).HasConversion<int>().IsRequired();
        builder.Property(r => r.QualifiedAtUtc).IsRequired();

        builder.Property(r => r.QueryLatitude).HasPrecision(9, 6);
        builder.Property(r => r.QueryLongitude).HasPrecision(9, 6);
        builder.Property(r => r.QueryAmid).HasMaxLength(60);

        builder.Property(r => r.ErrorCode).HasMaxLength(120);
        builder.Property(r => r.ErrorMessage).HasMaxLength(1000);

        builder.Property(r => r.Amid).HasMaxLength(60);
        builder.Property(r => r.CanonicalAddress).HasMaxLength(400);
        builder.Property(r => r.StreetNumber).HasMaxLength(40);
        builder.Property(r => r.StreetName).HasMaxLength(200);
        builder.Property(r => r.StreetType).HasMaxLength(40);
        builder.Property(r => r.Suburb).HasMaxLength(150);
        builder.Property(r => r.Town).HasMaxLength(150);
        builder.Property(r => r.Province).HasMaxLength(150);
        builder.Property(r => r.Region).HasMaxLength(150);
        builder.Property(r => r.Country).HasMaxLength(100);
        builder.Property(r => r.Latitude).HasPrecision(9, 6);
        builder.Property(r => r.Longitude).HasPrecision(9, 6);
        builder.Property(r => r.AddressStatus).HasMaxLength(100);
        builder.Property(r => r.DistanceMeters).HasPrecision(12, 2);
        builder.Property(r => r.DistanceText).HasMaxLength(60);
        builder.Property(r => r.MduVerification).HasMaxLength(500);
        builder.Property(r => r.AddressMessage).HasMaxLength(500);
        builder.Property(r => r.BuildingCandidatesJson).HasColumnType("nvarchar(max)");

        builder.Property(r => r.FibreAvailability).HasConversion<int>().IsRequired();
        builder.Property(r => r.FtthStatusSummary).HasMaxLength(300);
        builder.Property(r => r.FibreMaxSpeedMbps).HasPrecision(12, 2);
        builder.Property(r => r.AvailableProductCodes).HasMaxLength(500);
        builder.Property(r => r.EthernetProductCodes).HasMaxLength(300);
        builder.Property(r => r.FwaStatus).HasMaxLength(100);

        builder.Property(r => r.CustomerAddress).HasMaxLength(600);
        builder.Property(r => r.AddressMatch).HasConversion<int>().IsRequired();
        builder.Property(r => r.AddressMatchDetail).HasMaxLength(1000);
        builder.Property(r => r.AddressAcceptanceNote).HasMaxLength(500);

        builder.Property(r => r.MappingSku).HasMaxLength(20);
        builder.Property(r => r.MappingCapacity).HasMaxLength(20);
        builder.Property(r => r.MappingCapacityUom).HasMaxLength(20);
        builder.Property(r => r.ProductEligibility).HasConversion<int>().IsRequired();
        builder.Property(r => r.EligibilityReason).HasMaxLength(1000);

        builder.HasMany(r => r.Products)
            .WithOne(p => p.QualificationResult)
            .HasForeignKey(p => p.QualificationResultId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.OrderId);
        builder.HasIndex(r => r.Amid);
        builder.HasIndex(r => r.QualifiedAtUtc);
        builder.HasIndex(r => new { r.QueryLatitude, r.QueryLongitude, r.QualifiedAtUtc });
    }
}

public class OpenserveQualifiedProductConfiguration : IEntityTypeConfiguration<OpenserveQualifiedProduct>
{
    public void Configure(EntityTypeBuilder<OpenserveQualifiedProduct> builder)
    {
        builder.ToTable("OpenserveQualificationProducts");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.CreatedAtUtc).IsRequired();
        builder.Property(p => p.UpdatedAtUtc);
        builder.Property(p => p.RowVersion).IsRowVersion();

        builder.Property(p => p.InfrastructureType).HasMaxLength(60);
        builder.Property(p => p.FtthStatus).HasMaxLength(60);
        builder.Property(p => p.ServiceProviderId).HasMaxLength(60);
        builder.Property(p => p.FibreMaxSpeedMbps).HasPrecision(12, 2);
        builder.Property(p => p.ProductCode).HasMaxLength(20);
        builder.Property(p => p.ProductName).HasMaxLength(150);
        builder.Property(p => p.UpstreamSpeed).HasMaxLength(40);
        builder.Property(p => p.DownstreamSpeed).HasMaxLength(40);
        builder.Property(p => p.UpstreamMbps).HasPrecision(12, 2);
        builder.Property(p => p.DownstreamMbps).HasPrecision(12, 2);

        builder.HasIndex(p => p.QualificationResultId);
        builder.HasIndex(p => p.ProductCode);
    }
}
