namespace SmartFuture.Application.Customers.Admin.Dtos;

public class AdminCustomerListItemDto
{
    public Guid Id { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }

    public string AccountStatus { get; set; } = "Active";

    public string? Suburb { get; set; }
    public string? City { get; set; }
    public string? Province { get; set; }

    public string? ActivePackageName { get; set; }
    public string? ServiceType { get; set; }

    public decimal Outstanding { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
