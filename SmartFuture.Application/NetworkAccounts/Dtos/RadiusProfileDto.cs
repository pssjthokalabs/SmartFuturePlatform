namespace SmartFuture.Application.NetworkAccounts.Dtos;

public class RadiusProfileDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DownloadMbps { get; set; }
    public int UploadMbps { get; set; }
    public int? BurstMbps { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateRadiusProfileRequestDto
{
    public string Name { get; set; } = string.Empty;
    public int DownloadMbps { get; set; }
    public int UploadMbps { get; set; }
    public int? BurstMbps { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

public class UpdateRadiusProfileRequestDto
{
    public string Name { get; set; } = string.Empty;
    public int DownloadMbps { get; set; }
    public int UploadMbps { get; set; }
    public int? BurstMbps { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; }
    public string? Notes { get; set; }
}
