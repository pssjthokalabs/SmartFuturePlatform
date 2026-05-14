namespace SmartFuture.Application.Reports.Dtos;

public class ServicePackagePerformanceDto
{
    public Guid ServicePackageId { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string PackageType { get; set; } = string.Empty;
    public int OrderCount { get; set; }
    public int ActiveOrderCount { get; set; }
    public decimal TotalInvoiceAmount { get; set; }
    public decimal TotalPaidAmount { get; set; }
}
