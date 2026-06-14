using SmartFuture.Application.Payments.BillingOps.Dtos;
using SmartFuture.Shared.Results;

namespace SmartFuture.Application.Payments.BillingOps;

/// <summary>
/// Admin manual creation of a recurring SERVICE invoice when automatic
/// generation failed. Never marks paid, never charges, never calls a
/// provider. Duplicate-period creates are blocked unless force-created
/// (schedule-detached) with a reason + force confirmation phrase. Every
/// create is audited.
/// </summary>
public interface IManualInvoiceService
{
    Task<Result<ManualServiceInvoiceResultDto>> CreateManualServiceInvoiceAsync(
        ManualServiceInvoiceRequestDto request,
        Guid? actorUserId, string? ipAddress, string? userAgent,
        CancellationToken cancellationToken = default);
}
