namespace SmartFuture.Application.Installations.Dtos;

public class CreateInstallationRequestDto
{
    public Guid OrderId { get; set; }
    public DateTime? ScheduledForUtc { get; set; }
    public string? TechnicianName { get; set; }
    public string? TechnicianPhone { get; set; }
    public string? TechnicianEmail { get; set; }
    public Guid? TechnicianUserId { get; set; }
    public string? AdminNotes { get; set; }
}
