namespace SmartFuture.Application.Users.Admin.Dtos;

// Paged result + counts in one envelope so the Users page can render the
// table and the chip-row totals from a single round-trip.
public class AdminUsersSearchResultDto
{
    public IReadOnlyList<AdminUserListItemDto> Items { get; set; } = Array.Empty<AdminUserListItemDto>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public AdminUserTypeCountsDto Counts { get; set; } = new();
}
