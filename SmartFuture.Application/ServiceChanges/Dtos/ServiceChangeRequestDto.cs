using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Shared.Enums.ServiceChanges;

namespace SmartFuture.Application.ServiceChanges.Dtos;

// Wire-shape for both mobile + portal. Mirrors the entity 1:1 plus a
// few convenience labels so the UI can render without a second lookup.
public class ServiceChangeRequestDto
{
    public Guid   Id            { get; set; }
    public string RequestNumber { get; set; } = string.Empty;

    public Guid    UserId           { get; set; }
    public string? UserEmail        { get; set; }
    public string? UserFullName     { get; set; }
    public Guid    NetworkAccountId { get; set; }
    public string? NetworkAccountNumber { get; set; }
    public Guid?   OrderId          { get; set; }
    public string? OrderNumber      { get; set; }

    public Guid?   CurrentPackageId    { get; set; }
    public string  CurrentPackageName  { get; set; } = string.Empty;
    public decimal CurrentMonthlyPrice { get; set; }
    public ServicePackageBillingCycle CurrentBillingCycle { get; set; }

    public Guid    RequestedPackageId    { get; set; }
    public string  RequestedPackageName  { get; set; } = string.Empty;
    public decimal RequestedMonthlyPrice { get; set; }
    public ServicePackageBillingCycle RequestedBillingCycle { get; set; }

    public ServiceChangeType          ChangeType    { get; set; }
    public ServiceChangeEffectiveMode EffectiveMode { get; set; }
    public ServiceChangeStatus        Status        { get; set; }
    public ServiceChangeSource        Source        { get; set; }

    public decimal ProRataAmount        { get; set; }
    public int     ProRataCycleDays     { get; set; }
    public int     ProRataRemainingDays { get; set; }
    public DateTime EffectiveDateUtc    { get; set; }

    public Guid?   InvoiceId     { get; set; }
    public string? InvoiceNumber { get; set; }
    public Guid?   PaymentId     { get; set; }
    public string? PaymentNumber { get; set; }

    public string? CustomerNotes      { get; set; }
    public string? AdminNotes         { get; set; }
    public string? CancellationReason { get; set; }
    public string? RejectionReason    { get; set; }
    public string? FailureReason      { get; set; }

    public DateTime? AppliedAtUtc   { get; set; }
    public DateTime? CancelledAtUtc { get; set; }
    public DateTime? RejectedAtUtc  { get; set; }
    public DateTime? FailedAtUtc    { get; set; }

    public Guid?   LastStatusChangedByUserId    { get; set; }
    public string? LastStatusChangedByUserEmail { get; set; }

    public DateTime  CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}
