namespace SmartFuture.Infrastructure.Configuration;

// Bootstrap-seed configuration for the first Super Admin user.
//
// The seeder runs at startup, is idempotent, and **never** stores or
// logs the password. The password must be supplied via the hosting
// environment (e.g. IIS app-pool env vars) — see DEPLOYMENT_CONFIG.md.
//
//   SeedSuperAdmin__Enabled=true
//   SeedSuperAdmin__Email=developers@smartfuture.co.za
//   SeedSuperAdmin__PhoneNumber=0737942244
//   SeedSuperAdmin__FirstName=Developers
//   SeedSuperAdmin__LastName=Smart Future
//   SeedSuperAdmin__Password=<set in environment only>
//
// If `Enabled=false` or `Password` is blank, the seeder is a no-op.
// If the email already exists, the seeder only assigns the SuperAdmin /
// Admin roles when missing — it does NOT reset the password.
public class SeedSuperAdminSettings
{
    public const string SectionName = "SeedSuperAdmin";

    public bool Enabled { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string FirstName { get; set; } = "Super";
    public string LastName { get; set; } = "Admin";
    public string Password { get; set; } = string.Empty;
}
