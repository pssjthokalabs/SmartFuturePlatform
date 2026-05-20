namespace SmartFuture.Application.Users.Admin.Dtos;

public class AdminUsersFilterRequestDto
{
    // "All" / "Customer" / "Admin" / "Agent" / "Technician" / "Support".
    // Null or empty is treated as "All".
    public string? Type { get; set; }
    public string? Search { get; set; }
    public string? Status { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}
