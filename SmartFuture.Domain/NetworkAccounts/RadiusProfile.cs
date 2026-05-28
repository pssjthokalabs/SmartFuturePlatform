using SmartFuture.Domain.Common;

namespace SmartFuture.Domain.NetworkAccounts;

public class RadiusProfile : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public int DownloadMbps { get; set; }
    public int UploadMbps { get; set; }
    public int? BurstMbps { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}
