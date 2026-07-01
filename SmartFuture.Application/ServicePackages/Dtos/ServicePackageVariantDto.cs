namespace SmartFuture.Application.ServicePackages.Dtos;

// Public/admin shape for a single orderable package variant. The public
// package result returns only active variants (sorted by DisplayOrder
// then Name); the admin get-by-id returns all so inactive ones can be
// re-enabled.
public class ServicePackageVariantDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }

    // Null → inherit the package's activation fee at checkout.
    public decimal? InstallationFee { get; set; }
    // Null → inherit the package's free-activation flag at checkout.
    public bool? HasFreeInstallation { get; set; }

    public bool IsActive { get; set; }
    public int DisplayOrder { get; set; }
}

// Admin input row for create/update. Id is null for a brand-new variant
// and set for an existing one being edited (the service upserts by Id,
// deletes the rows the admin removed).
public class ServicePackageVariantInputDto
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal? InstallationFee { get; set; }
    public bool? HasFreeInstallation { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
}
