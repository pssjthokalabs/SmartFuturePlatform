namespace SmartFuture.Shared.Constants;

public static class SystemRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Admin = "Admin";
    public const string Customer = "Customer";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        SuperAdmin,
        Admin,
        Customer
    };
}
