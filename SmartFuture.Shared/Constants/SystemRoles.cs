namespace SmartFuture.Shared.Constants;

public static class SystemRoles
{
    public const string Admin = "Admin";
    public const string Customer = "Customer";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Admin,
        Customer
    };
}
