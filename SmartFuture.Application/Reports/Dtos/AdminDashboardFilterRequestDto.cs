namespace SmartFuture.Application.Reports.Dtos;

public class AdminDashboardFilterRequestDto
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    public Guid? ServicePackageId { get; set; }
}
