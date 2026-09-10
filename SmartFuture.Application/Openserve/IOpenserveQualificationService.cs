using SmartFuture.Domain.Orders;

namespace SmartFuture.Application.Openserve;

/// <summary>
/// Runs the Openserve Product Qualification API (spec §3) against an
/// order's installation address and writes the result directly onto
/// the Order — this is the ONLY writer of Order.OpenserveAmId/
/// OpenserveBuildingNumId/OpenserveQualifiedAtUtc/
/// OpenserveQualificationFailureReason. Never throws; a failed lookup
/// just leaves OpenserveAmId null with a reason recorded, and order
/// creation proceeds regardless — OpenserveOrderSubmissionService
/// already blocks (visibly) on a missing AMID later.
/// </summary>
public interface IOpenserveQualificationService
{
    /// <summary>
    /// Mutates the given (already in-memory, not-yet-necessarily-saved)
    /// Order entity in place. Caller is responsible for persisting the
    /// Order itself (this method independently persists its own
    /// OpenserveIntegrationLog row for traceability regardless of
    /// whether the Order save happens before or after).
    /// </summary>
    Task QualifyOrderAsync(Order order, CancellationToken cancellationToken = default);
}
