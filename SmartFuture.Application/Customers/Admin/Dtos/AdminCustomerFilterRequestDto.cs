namespace SmartFuture.Application.Customers.Admin.Dtos;

public class AdminCustomerFilterRequestDto
{
    public string? Search { get; set; }
    public string? Status { get; set; }
    public string? ServiceType { get; set; }
    public string? Balance { get; set; }
    public int? Page { get; set; }
    public int? PageSize { get; set; }
}
