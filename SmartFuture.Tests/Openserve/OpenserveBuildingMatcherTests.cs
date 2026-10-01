using SmartFuture.Application.Openserve;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Postman UC 1: for a multi dwelling unit, buildingName / floor / unit /
// buildingNumId are mandatory and must be "exactly per product
// qualification API". The matcher picks the customer's own row without
// ever guessing between ambiguous candidates.
public class OpenserveBuildingMatcherTests
{
    // PDF §3.2 sample rows (AMID 50782408 — the same AMID as the Postman qualification sample).
    private static readonly IReadOnlyList<OpenserveQualificationBuilding> EaglesLanding = new List<OpenserveQualificationBuilding>
    {
        new("50782408", "395208", "617914", "290107", "1", "EAGLES LANDING SHOPPING CENTRE", "GROUND"),
        new("50782408", "786154", "617914", "290107", "12", "EAGLES LANDING SHOPPING CENTRE", "GROUND"),
        new("50782408", "783682", "617914", "290107", "18", "EAGLES LANDING SHOPPING CENTRE", "GROUND")
    };

    [Fact]
    public void SingleRow_IsReturnedAsIs_EvenWithoutUnitNumber()
    {
        var only = new List<OpenserveQualificationBuilding> { new("1000497", "42", null, null, "3F", "THE OVAL BLOCK 3", "2") };

        var match = OpenserveBuildingMatcher.Match(only, unitNumber: null, buildingComplexName: null);

        Assert.Equal("42", match!.BldNumId);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("Unit 12")]
    [InlineData("unit12")]
    [InlineData("#12")]
    [InlineData(" Flat 12 ")]
    public void MultipleRows_MatchedByCustomerUnitNumber(string unitNumber)
    {
        var match = OpenserveBuildingMatcher.Match(EaglesLanding, unitNumber, buildingComplexName: null);

        Assert.NotNull(match);
        Assert.Equal("786154", match!.BldNumId);
        Assert.Equal("EAGLES LANDING SHOPPING CENTRE", match.BuildingName);
        Assert.Equal("GROUND", match.Floor);
        Assert.Equal("12", match.Num);
    }

    [Fact]
    public void MultipleRows_NoUnitNumber_ReturnsNull()
        => Assert.Null(OpenserveBuildingMatcher.Match(EaglesLanding, unitNumber: null, buildingComplexName: "Eagles Landing"));

    [Fact]
    public void MultipleRows_UnknownUnit_ReturnsNull()
        => Assert.Null(OpenserveBuildingMatcher.Match(EaglesLanding, unitNumber: "99", buildingComplexName: null));

    [Fact]
    public void SameUnitInTwoBuildings_DisambiguatedByBuildingName_OrNullWhenStillAmbiguous()
    {
        var twoBlocks = new List<OpenserveQualificationBuilding>
        {
            new("78056293", "1001", "A", null, "3F", "THE OVAL BLOCK 1", "2"),
            new("78056293", "1003", "C", null, "3F", "THE OVAL BLOCK 3", "2")
        };

        Assert.Equal("1003", OpenserveBuildingMatcher.Match(twoBlocks, "3F", "The Oval Block 3")!.BldNumId);
        Assert.Null(OpenserveBuildingMatcher.Match(twoBlocks, "3F", buildingComplexName: null));
        Assert.Null(OpenserveBuildingMatcher.Match(twoBlocks, "3F", "The Oval"));
    }

    [Fact]
    public void NoRows_ReturnsNull()
    {
        Assert.Null(OpenserveBuildingMatcher.Match(null, "12", null));
        Assert.Null(OpenserveBuildingMatcher.Match(new List<OpenserveQualificationBuilding>(), "12", null));
    }
}
