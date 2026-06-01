namespace SmartFuture.Application.Installations.Dtos;

/// <summary>
/// Narrow assign-technician body (go-live Issue 5). Used by the
/// dedicated POST /api/installations/admin/{id}/assign-technician
/// endpoint so the admin Schedule Installation modal and the
/// Installation Detail "Assign Technician" panel never trip
/// AddressLine1 validation on a partial update.
///
/// Pass <c>TechnicianUserId = Guid.Empty</c> to explicitly unassign.
/// </summary>
public class AdminAssignTechnicianRequestDto
{
    public Guid? TechnicianUserId { get; set; }
    public string? AdminNotes { get; set; }
}
