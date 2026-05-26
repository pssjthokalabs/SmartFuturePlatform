using SmartFuture.Shared.Enums.ServiceChanges;

namespace SmartFuture.Application.ServiceChanges.Dtos;

// Backend-calculated pro-rata preview for the customer wizard. The
// mobile/portal UI must render the numbers exactly as returned here
// and NEVER recompute client-side — the brief is explicit that the
// server owns the math.
public class ServiceChangePreviewDto
{
    public Guid    NetworkAccountId      { get; set; }
    public Guid    RequestedPackageId    { get; set; }

    public string  CurrentPackageName    { get; set; } = string.Empty;
    public decimal CurrentMonthlyPrice   { get; set; }
    public string  RequestedPackageName  { get; set; } = string.Empty;
    public decimal RequestedMonthlyPrice { get; set; }

    public ServiceChangeType          ChangeType    { get; set; }
    public ServiceChangeEffectiveMode EffectiveMode { get; set; }

    /// <summary>0 for downgrades (no charge today).</summary>
    public decimal  DueTodayAmount    { get; set; }
    public int      ProRataCycleDays  { get; set; }
    public int      ProRataRemainingDays { get; set; }
    public DateTime EffectiveDateUtc  { get; set; }
    public DateTime? NextCycleAnchorUtc { get; set; }

    /// <summary>Display-ready single sentence summary, e.g. "Pay R74 today,
    /// your next monthly billing amount will be R570".</summary>
    public string Summary { get; set; } = string.Empty;
}
