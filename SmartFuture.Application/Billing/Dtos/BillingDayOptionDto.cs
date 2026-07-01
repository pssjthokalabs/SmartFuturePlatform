namespace SmartFuture.Application.Billing.Dtos;

public class BillingDayOptionDto
{
    public Guid Id { get; set; }
    public int Day { get; set; }
    public string Label { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class CreateBillingDayOptionRequestDto
{
    public int Day { get; set; }
    public string? Label { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
}

public class UpdateBillingDayOptionRequestDto
{
    public int Day { get; set; }
    public string? Label { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public int DisplayOrder { get; set; }
}
