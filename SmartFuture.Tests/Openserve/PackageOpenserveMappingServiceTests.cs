using Microsoft.Extensions.Logging.Abstractions;
using SmartFuture.Application.Openserve;
using SmartFuture.Application.Openserve.Dtos;
using SmartFuture.Domain.Openserve;
using SmartFuture.Domain.Orders;
using SmartFuture.Domain.ServicePackages;
using SmartFuture.Shared.Enums.Openserve;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Covers brief §4 ("Build a durable mapping for fibre packages...An
// unmapped package must never silently submit") and the Appendix D
// catalogue validation added on top of it.
public class PackageOpenserveMappingServiceTests
{
    private static PackageOpenserveMappingService BuildService(SqliteTestDbFixture fixture) =>
        new(fixture.AppDbContext, NullLogger<PackageOpenserveMappingService>.Instance);

    private static ServicePackage NewFibrePackage(string name, ServicePackageStatus status = ServicePackageStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        Type = ServicePackageType.Fibre,
        Status = status,
        Name = name,
        Price = 899m,
        BillingCycle = ServicePackageBillingCycle.Monthly,
        CreatedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public async Task CreateAsync_WithCatalogueValidSkuAndCapacity_Succeeds()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = NewFibrePackage("Fibre 75Mbps");
        fixture.DbContext.ServicePackages.Add(package);
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var result = await service.CreateAsync(new CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id,
            OpenserveProductName = "Openserve Fibre Connect",
            Sku = "OFC",
            Capacity = "75",
            CapacityUom = "Mbps",
            IsEnabled = true
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("OFC", result.Data!.Sku);
        Assert.True(result.Data.IsEnabled);
    }

    [Fact]
    public async Task CreateAsync_WithUnknownSku_FailsValidation()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = NewFibrePackage("Fibre 75Mbps");
        fixture.DbContext.ServicePackages.Add(package);
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var result = await service.CreateAsync(new CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id,
            OpenserveProductName = "Openserve Fibre Connect",
            Sku = "OFX", // not a real SKU
            Capacity = "75",
            CapacityUom = "Mbps"
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("not a SKU documented", result.Message);

        // A failed validation must not leave a row behind — the whole
        // point is that an unmapped/misconfigured package stays absent
        // from the mapping table, not present-but-broken.
        var stillUnmapped = await service.ListUnmappedFibrePackagesAsync();
        Assert.Contains(stillUnmapped.Data!, p => p.ServicePackageId == package.Id);
    }

    [Fact]
    public async Task CreateAsync_WithValidSkuButWrongCapacity_FailsValidation()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = NewFibrePackage("Fibre 999Mbps");
        fixture.DbContext.ServicePackages.Add(package);
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var result = await service.CreateAsync(new CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id,
            OpenserveProductName = "Openserve Fibre Connect",
            Sku = "OFC",
            Capacity = "999", // not a documented OFC speed
            CapacityUom = "Mbps"
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("not a documented valid speed", result.Message);
    }

    [Fact]
    public async Task CreateAsync_Duplicate_ReturnsConflict()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = NewFibrePackage("Fibre 50Mbps");
        fixture.DbContext.ServicePackages.Add(package);
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var request = new CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id,
            OpenserveProductName = "Openserve Fibre Connect",
            Sku = "OFC",
            Capacity = "50",
            CapacityUom = "Mbps Lite"
        };

        var first = await service.CreateAsync(request);
        Assert.True(first.IsSuccess);

        var second = await service.CreateAsync(request);
        Assert.False(second.IsSuccess);
        Assert.Equal("CONFLICT", second.Code);
    }

    [Fact]
    public async Task ListUnmappedFibrePackagesAsync_ExcludesMappedNonFibreAndArchivedPackages()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();

        var mappedFibre = NewFibrePackage("Fibre 100Mbps (mapped)");
        var unmappedFibre = NewFibrePackage("Fibre 200Mbps (unmapped)");
        var archivedFibre = NewFibrePackage("Fibre old (archived)", ServicePackageStatus.Archived);
        var voicePackage = new ServicePackage
        {
            Id = Guid.NewGuid(), Type = ServicePackageType.Voice, Status = ServicePackageStatus.Active,
            Name = "Voice Basic", Price = 199m, BillingCycle = ServicePackageBillingCycle.Monthly,
            CreatedAtUtc = DateTime.UtcNow
        };

        fixture.DbContext.ServicePackages.AddRange(mappedFibre, unmappedFibre, archivedFibre, voicePackage);
        fixture.DbContext.PackageOpenserveMappings.Add(new PackageOpenserveMapping
        {
            Id = Guid.NewGuid(), ServicePackageId = mappedFibre.Id,
            OpenserveProductName = "Openserve Fibre Connect", Sku = "OFC", Capacity = "100", CapacityUom = "Mbps",
            IsEnabled = true, CreatedAtUtc = DateTime.UtcNow
        });
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var result = await service.ListUnmappedFibrePackagesAsync();

        Assert.True(result.IsSuccess);
        var ids = result.Data!.Select(p => p.ServicePackageId).ToList();
        Assert.Contains(unmappedFibre.Id, ids);
        Assert.DoesNotContain(mappedFibre.Id, ids);
        Assert.DoesNotContain(archivedFibre.Id, ids);
        Assert.DoesNotContain(voicePackage.Id, ids);
    }

    [Fact]
    public async Task DeleteAsync_WhenReferencedByAnOpenserveOrder_DisablesInsteadOfDeleting()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var package = NewFibrePackage("Fibre 50Mbps");
        fixture.DbContext.ServicePackages.Add(package);
        await fixture.DbContext.SaveChangesAsync();

        var service = BuildService(fixture);
        var created = await service.CreateAsync(new CreatePackageOpenserveMappingRequestDto
        {
            ServicePackageId = package.Id,
            OpenserveProductName = "Openserve Fibre Connect",
            Sku = "OFC",
            Capacity = "50",
            CapacityUom = "Mbps",
            IsEnabled = true
        });

        var user = TestEntityFactory.CreateUser(fixture.AppDbContext, "package-mapping-delete@example.com");
        var order = TestEntityFactory.CreateOrder(fixture.AppDbContext, user, package, orderNumber: "ORD-TEST-1");
        fixture.DbContext.OpenserveOrders.Add(new OpenserveOrder
        {
            Id = Guid.NewGuid(), OrderId = order.Id, ExternalReferenceNumber = "EXT-1",
            OrderType = "Sales Order", NormalizedStatus = OpenserveProvisioningStatus.Submitted,
            PackageOpenserveMappingId = created.Data!.Id, CreatedAtUtc = DateTime.UtcNow
        });
        await fixture.DbContext.SaveChangesAsync();

        var deleteResult = await service.DeleteAsync(created.Data!.Id);

        Assert.True(deleteResult.IsSuccess);
        Assert.Contains("disabled instead of deleted", deleteResult.Message);

        var stillThere = await service.GetByServicePackageIdAsync(package.Id);
        Assert.True(stillThere.IsSuccess);
        Assert.False(stillThere.Data!.IsEnabled);
    }
}
