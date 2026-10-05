using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SmartFuture.Application.Openserve;
using SmartFuture.Domain.CoverageRequests;
using SmartFuture.Domain.Orders;
using SmartFuture.Shared.Enums.ServicePackages;
using SmartFuture.Tests.Infrastructure;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Brief §3-4/TEST REQUIREMENTS: AMID persistence into Order, missing
// AMID recorded with a visible reason, coordinate fallback to a linked
// CoverageRequest, disabled-integration no-op.
public class OpenserveQualificationServiceTests
{
    private static IOpenserveRuntimeConfigProvider Monitor(bool enabled = true)
    {
        var m = new Mock<IOpenserveRuntimeConfigProvider>();
        m.Setup(x => x.Current).Returns(new OpenserveFulfilmentSettings { Enabled = enabled, BaseUrl = "https://testapitrx.openserve.co.za", WsIspCode = "ws-ispcode" });
        return m.Object;
    }

    /// <summary>
    /// Address verification (FORCEVERIFY) answers with one Openserve record that
    /// is the order's own address ("61 OAK AVE") under <paramref name="amid"/>;
    /// the AMID qualification answers with <paramref name="amidAnswer"/>.
    /// </summary>
    private static Mock<IOpenserveApiClient> ClientAnswering(OpenserveApiCallResult<OpenserveQualificationOutcome> amidAnswer, string amid) =>
        OpenserveQualificationOrchestrationTests.ClientWithVerify(new List<OpenserveCreateOrderCommand>(),
            OpenserveEvidenceFixtures.Verify((amid, "61 OAK AVE HIGHVELD CENTURION", 0m, -26m, 27m)), amidAnswer);

    private static Order NewOrder(decimal? lat = -26m, decimal? lon = 27m, Guid? coverageRequestId = null) => new()
    {
        Id = Guid.NewGuid(), OrderNumber = $"ORD-{Guid.NewGuid():N}"[..12], PackageType = ServicePackageType.Fibre,
        Latitude = lat, Longitude = lon, CoverageRequestId = coverageRequestId, AddressLine1 = "61 Oak Ave"
    };

    [Fact]
    public async Task QualifyOrderAsync_Success_PersistsAmidOntoOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var client = ClientAnswering(OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveQualificationOutcome("1000497", "42", 1, "61 Oak Ave", "Working", 200m, "Mbps")), "1000497");

        var service = new OpenserveQualificationService(fixture.AppDbContext, client.Object, Monitor(), NullLogger<OpenserveQualificationService>.Instance);
        var order = NewOrder();

        await service.QualifyOrderAsync(order);

        Assert.Equal("1000497", order.OpenserveAmId);
        Assert.Equal("42", order.OpenserveBuildingNumId);
        Assert.Null(order.OpenserveQualificationFailureReason);
        Assert.NotNull(order.OpenserveQualifiedAtUtc);
    }

    [Fact]
    public async Task QualifyOrderAsync_MultipleBuildingMatches_LeavesBuildingNumIdNull_ButKeepsAmid()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var client = ClientAnswering(OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveQualificationOutcome("50782408", null, 3, "some MDU address", "Working", 500m, "Mbps")), "50782408");

        var service = new OpenserveQualificationService(fixture.AppDbContext, client.Object, Monitor(), NullLogger<OpenserveQualificationService>.Instance);
        var order = NewOrder();

        await service.QualifyOrderAsync(order);

        Assert.Equal("50782408", order.OpenserveAmId); // AMID always usable
        Assert.Null(order.OpenserveBuildingNumId);      // buildingNumId is optional — ambiguity doesn't block
        Assert.Contains("3", order.OpenserveQualificationFailureReason);
    }

    [Fact]
    public async Task QualifyOrderAsync_Mdu_MatchesCustomerUnit_AndPersistsQualificationBuildingValuesVerbatim()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var buildings = new List<OpenserveQualificationBuilding>
        {
            new("50782408", "395208", "617914", "290107", "1", "EAGLES LANDING SHOPPING CENTRE", "GROUND"),
            new("50782408", "786154", "617914", "290107", "12", "EAGLES LANDING SHOPPING CENTRE", "GROUND"),
            new("50782408", "783682", "617914", "290107", "18", "EAGLES LANDING SHOPPING CENTRE", "GROUND")
        };
        var client = ClientAnswering(OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveQualificationOutcome("50782408", null, 3, "4682 SYSIE ST", "Working", 500m, "Mbps", Buildings: buildings),
                requestHeadersJson: """{"MessageID":"x","FromLocation":"ws-marut","api_key":"***REDACTED***"}"""), "50782408");

        var service = new OpenserveQualificationService(fixture.AppDbContext, client.Object, Monitor(), NullLogger<OpenserveQualificationService>.Instance);
        var order = NewOrder();
        order.UnitNumber = "Unit 12";
        order.BuildingComplexName = "Eagles Landing";

        await service.QualifyOrderAsync(order);

        Assert.Equal("50782408", order.OpenserveAmId);
        Assert.Equal("786154", order.OpenserveBuildingNumId);
        Assert.Equal("EAGLES LANDING SHOPPING CENTRE", order.OpenserveBuildingName);
        Assert.Equal("GROUND", order.OpenserveFloor);
        Assert.Equal("12", order.OpenserveUnit);
        Assert.Null(order.OpenserveQualificationFailureReason);
        // Customer free text is untouched — only the Openserve* fields carry qualification values.
        Assert.Equal("Unit 12", order.UnitNumber);

        // Two calls: address verification (FORCEVERIFY) then the AMID qualification — both logged, both redacted.
        var logs = await fixture.DbContext.OpenserveIntegrationLogs.ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Contains("REDACTED", l.RequestHeadersJson));
        Assert.Contains(logs, l => l.RequestHeadersJson!.Contains("ws-marut"));
    }

    [Fact]
    public async Task QualifyOrderAsync_Mdu_UnitNotMatched_LeavesBuildingFieldsBlank_WithReason()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var buildings = new List<OpenserveQualificationBuilding>
        {
            new("50782408", "395208", null, null, "1", "EAGLES LANDING", "GROUND"),
            new("50782408", "786154", null, null, "12", "EAGLES LANDING", "GROUND")
        };
        var client = ClientAnswering(OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveQualificationOutcome("50782408", null, 2, null, "Working", 500m, "Mbps", Buildings: buildings)), "50782408");

        var service = new OpenserveQualificationService(fixture.AppDbContext, client.Object, Monitor(), NullLogger<OpenserveQualificationService>.Instance);
        var order = NewOrder();
        order.UnitNumber = "77";

        await service.QualifyOrderAsync(order);

        Assert.Equal("50782408", order.OpenserveAmId);
        Assert.Null(order.OpenserveBuildingNumId);
        Assert.Null(order.OpenserveBuildingName);
        Assert.Null(order.OpenserveUnit);
        Assert.Contains("77", order.OpenserveQualificationFailureReason);
    }

    [Fact]
    public async Task QualifyOrderAsync_NoAmidReturned_RecordsVisibleReason()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveQualificationOutcome>.Failure(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}", "-1", "No coverage found for this location."));

        var service = new OpenserveQualificationService(fixture.AppDbContext, client.Object, Monitor(), NullLogger<OpenserveQualificationService>.Instance);
        var order = NewOrder();

        await service.QualifyOrderAsync(order);

        Assert.Null(order.OpenserveAmId);
        Assert.NotNull(order.OpenserveQualificationFailureReason);
        Assert.Contains("No coverage found", order.OpenserveQualificationFailureReason);
        Assert.NotNull(order.OpenserveQualifiedAtUtc); // attempt was made, even though it failed
    }

    [Fact]
    public async Task QualifyOrderAsync_NoCoordinatesAnywhere_RecordsReason_DoesNotThrow()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var client = new Mock<IOpenserveApiClient>();
        var service = new OpenserveQualificationService(fixture.AppDbContext, client.Object, Monitor(), NullLogger<OpenserveQualificationService>.Instance);
        var order = NewOrder(lat: null, lon: null);

        await service.QualifyOrderAsync(order);

        Assert.Null(order.OpenserveAmId);
        Assert.NotNull(order.OpenserveQualificationFailureReason);
        client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QualifyOrderAsync_FallsBackToCoverageRequestCoordinates_WhenOrderHasNone()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var coverageRequest = new CoverageRequest
        {
            Id = Guid.NewGuid(), AddressLine1 = "61 Oak Ave", Latitude = -25.5m, Longitude = 28.1m,
            CreatedAtUtc = DateTime.UtcNow
        };
        fixture.DbContext.CoverageRequests.Add(coverageRequest);
        await fixture.DbContext.SaveChangesAsync();

        var client = ClientAnswering(OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveQualificationOutcome("1000497", null, 0, null, "Working", 100m, "Mbps")), "1000497");

        var service = new OpenserveQualificationService(fixture.AppDbContext, client.Object, Monitor(), NullLogger<OpenserveQualificationService>.Instance);
        var order = NewOrder(lat: null, lon: null, coverageRequestId: coverageRequest.Id);

        await service.QualifyOrderAsync(order);

        Assert.Equal("1000497", order.OpenserveAmId);
        // The coverage request's point is what address verification was asked about.
        client.Verify(c => c.QualifyAsync(It.Is<OpenserveQualificationQuery>(q => q.ForceVerify && q.Latitude == -25.5m && q.Longitude == 28.1m), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QualifyOrderAsync_WhenDisabled_DoesNothing()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var client = new Mock<IOpenserveApiClient>();
        var service = new OpenserveQualificationService(fixture.AppDbContext, client.Object, Monitor(enabled: false), NullLogger<OpenserveQualificationService>.Instance);
        var order = NewOrder();

        await service.QualifyOrderAsync(order);

        Assert.Null(order.OpenserveAmId);
        Assert.Null(order.OpenserveQualifiedAtUtc); // never attempted, not "attempted and failed"
        client.Verify(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
