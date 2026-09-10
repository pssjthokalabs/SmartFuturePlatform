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

    private static Order NewOrder(decimal? lat = -26m, decimal? lon = 27m, Guid? coverageRequestId = null) => new()
    {
        Id = Guid.NewGuid(), OrderNumber = $"ORD-{Guid.NewGuid():N}"[..12], PackageType = ServicePackageType.Fibre,
        Latitude = lat, Longitude = lon, CoverageRequestId = coverageRequestId, AddressLine1 = "61 Oak Ave"
    };

    [Fact]
    public async Task QualifyOrderAsync_Success_PersistsAmidOntoOrder()
    {
        await using var fixture = await SqliteTestDbFixture.CreateAsync();
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveQualificationOutcome("1000497", "42", 1, "61 Oak Ave", "Working", 200m, "Mbps")));

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
        var client = new Mock<IOpenserveApiClient>();
        client.Setup(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveQualificationOutcome("50782408", null, 3, "some MDU address", "Working", 500m, "Mbps")));

        var service = new OpenserveQualificationService(fixture.AppDbContext, client.Object, Monitor(), NullLogger<OpenserveQualificationService>.Instance);
        var order = NewOrder();

        await service.QualifyOrderAsync(order);

        Assert.Equal("50782408", order.OpenserveAmId); // AMID always usable
        Assert.Null(order.OpenserveBuildingNumId);      // buildingNumId is optional — ambiguity doesn't block
        Assert.Contains("3", order.OpenserveQualificationFailureReason);
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

        var client = new Mock<IOpenserveApiClient>();
        OpenserveQualificationQuery? capturedQuery = null;
        client.Setup(c => c.QualifyAsync(It.IsAny<OpenserveQualificationQuery>(), It.IsAny<CancellationToken>()))
            .Callback<OpenserveQualificationQuery, CancellationToken>((q, _) => capturedQuery = q)
            .ReturnsAsync(OpenserveApiCallResult<OpenserveQualificationOutcome>.Success(
                Guid.NewGuid().ToString(), "GET", "endpoint", 200, "", "{}",
                new OpenserveQualificationOutcome("1000497", null, 0, null, "Working", 100m, "Mbps")));

        var service = new OpenserveQualificationService(fixture.AppDbContext, client.Object, Monitor(), NullLogger<OpenserveQualificationService>.Instance);
        var order = NewOrder(lat: null, lon: null, coverageRequestId: coverageRequest.Id);

        await service.QualifyOrderAsync(order);

        Assert.Equal("1000497", order.OpenserveAmId);
        Assert.Equal(-25.5m, capturedQuery!.Latitude);
        Assert.Equal(28.1m, capturedQuery.Longitude);
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
