namespace SmartFuture.Application.Reports.Dtos;

public class RegionalDemandDto
{
    public string? Province { get; set; }
    public string? City { get; set; }
    public string? Suburb { get; set; }
    public int CoverageRequestCount { get; set; }
    public int OrderCount { get; set; }
    public int InstallationCount { get; set; }
}
