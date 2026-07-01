using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SmartFuture.Domain.Coverage;

namespace SmartFuture.Infrastructure.Data.Configurations.Coverage;

public class CoverageMapRuleConfiguration : IEntityTypeConfiguration<CoverageMapRule>
{
    public void Configure(EntityTypeBuilder<CoverageMapRule> builder)
    {
        builder.ToTable("CoverageMapRules");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.CreatedAtUtc).IsRequired();
        builder.Property(r => r.UpdatedAtUtc);
        builder.Property(r => r.RowVersion).IsRowVersion();

        builder.Property(r => r.Name)
            .IsRequired()
            .HasMaxLength(200);
        builder.Property(r => r.MatchText)
            .IsRequired()
            .HasMaxLength(200);
        builder.Property(r => r.Notes).HasMaxLength(1000);

        builder.Property(r => r.RuleType).HasConversion<int>().IsRequired();
        builder.Property(r => r.MatchMode).HasConversion<int>().IsRequired();
        builder.Property(r => r.AllowedComponents).HasConversion<int>().IsRequired();
        builder.Property(r => r.IsActive).IsRequired();
        builder.Property(r => r.Priority).IsRequired();

        // Query the evaluator hits: TryEvaluate reads
        //   WHERE IsActive = 1 AND RuleType = <n> ORDER BY Priority.
        // Compound index on (IsActive, RuleType, Priority) covers both
        // the include and exclude passes with a single seek.
        builder.HasIndex(r => new { r.IsActive, r.RuleType, r.Priority });
        builder.HasIndex(r => r.MatchText);
    }
}
