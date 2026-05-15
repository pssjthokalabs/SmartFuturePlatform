namespace SmartFuture.Application.Dashboard.Dtos;

public class AdminPaymentIssueDto
{
    public Guid Id { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string? Bank { get; set; }
    public string? LastResult { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = string.Empty;
}
