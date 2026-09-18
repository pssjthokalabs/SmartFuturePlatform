using SmartFuture.Application.Orders;
using SmartFuture.Shared.Enums.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using Xunit;

namespace SmartFuture.Tests.Orders;

// Residence/property type validation — the exact rules from the brief:
// required for Fibre, Apartment requires a unit number, House requires
// neither, every other type is captured but not hard-required.
public class PropertyTypeValidationTests
{
    [Fact]
    public void Fibre_WithoutPropertyType_IsRejected()
    {
        var result = PropertyTypeValidation.Validate(ServicePackageType.Fibre, null, null, null);
        Assert.NotNull(result);
        Assert.Contains("PropertyType is required", result);
    }

    [Theory]
    [InlineData(ServicePackageType.Security)]
    [InlineData(ServicePackageType.LTE)]
    [InlineData(ServicePackageType.Wireless)]
    [InlineData(ServicePackageType.WiFi)]
    [InlineData(ServicePackageType.Voice)]
    public void NonFibrePackageTypes_WithoutPropertyType_AreNotRejected(ServicePackageType packageType)
    {
        var result = PropertyTypeValidation.Validate(packageType, null, null, null);
        Assert.Null(result);
    }

    [Fact]
    public void Fibre_WithPropertyType_House_IsAccepted()
    {
        var result = PropertyTypeValidation.Validate(ServicePackageType.Fibre, PropertyType.House, null, null);
        Assert.Null(result);
    }

    [Fact]
    public void Apartment_WithoutUnitNumber_IsRejected()
    {
        var result = PropertyTypeValidation.Validate(ServicePackageType.Fibre, PropertyType.Apartment, "Waterfall Heights", null);
        Assert.NotNull(result);
        Assert.Contains("UnitNumber is required", result);
    }

    [Fact]
    public void Apartment_WithBlankUnitNumber_IsRejected()
    {
        var result = PropertyTypeValidation.Validate(ServicePackageType.Fibre, PropertyType.Apartment, "Waterfall Heights", "   ");
        Assert.NotNull(result);
    }

    [Fact]
    public void Apartment_WithUnitNumber_IsAccepted()
    {
        var result = PropertyTypeValidation.Validate(ServicePackageType.Fibre, PropertyType.Apartment, "Waterfall Heights", "14B");
        Assert.Null(result);
    }

    [Theory]
    [InlineData(PropertyType.Townhouse)]
    [InlineData(PropertyType.ComplexEstate)]
    [InlineData(PropertyType.StudentResidence)]
    [InlineData(PropertyType.BusinessOffice)]
    [InlineData(PropertyType.Duplex)]
    [InlineData(PropertyType.Other)]
    public void NonApartmentPropertyTypes_WithoutBuildingOrUnitInfo_AreNotHardRequired(PropertyType propertyType)
    {
        // Per the brief: "Complex/Estate/Townhouse should capture...
        // where applicable" / "Business/Office should support..." —
        // softer language than Apartment's explicit "should require".
        // These are collected in the UI but not blocked server-side.
        var result = PropertyTypeValidation.Validate(ServicePackageType.Fibre, propertyType, null, null);
        Assert.Null(result);
    }

    [Fact]
    public void BuildingComplexName_OverMaxLength_IsRejected()
    {
        var result = PropertyTypeValidation.Validate(
            ServicePackageType.Fibre, PropertyType.ComplexEstate, new string('a', 201), null);
        Assert.NotNull(result);
    }

    [Fact]
    public void UnitNumber_OverMaxLength_IsRejected()
    {
        var result = PropertyTypeValidation.Validate(
            ServicePackageType.Fibre, PropertyType.Apartment, "Waterfall Heights", new string('1', 51));
        Assert.NotNull(result);
    }
}
