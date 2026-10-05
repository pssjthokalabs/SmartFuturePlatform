using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Openserve;
using SmartFuture.Infrastructure.Openserve;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Brief §3/TEST REQUIREMENTS: Product Qualification request construction
// and AMID extraction — verified against the spec's own §3.2 worked
// example response shape (nested Results.payload.AddressInfo, SCREAMING_
// SNAKE building fields).
public class OpenserveQualificationClientTests
{
    private static OpenserveFulfilmentSettings Settings() => new()
    {
        Enabled = true, BaseUrl = "https://testapitrx.openserve.co.za", ApiKey = "test-key", WsIspCode = "ws-ispcode"
    };

    private static (OpenserveApiClient client, RecordingHandler handler) Build(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RecordingHandler(_ => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var httpClient = new HttpClient(handler);
        var configProvider = new Mock<IOpenserveRuntimeConfigProvider>();
        configProvider.Setup(m => m.Current).Returns(Settings());
        return (new OpenserveApiClient(httpClient, configProvider.Object, NullLogger<OpenserveApiClient>.Instance), handler);
    }

    // Spec §3.2 Example 2's exact worked response (MDU address, 3 units).
    private const string MduResponse = """
        {
          "errorCode": 0,
          "errorString": "OK",
          "message": "",
          "Results": {
            "payload": {
              "AddressInfo": {
                "AMID": "50782408",
                "LR_Address": "4682 SYSIE ST RAND PARK RIDGE X 88 RANDBURG",
                "LR_SUBURB": "RAND PARK RIDGE X 88",
                "LR_TOWN": "RANDBURG",
                "LR_PROVINCE": "GAUTENG",
                "MDU_Verification": "Order subject to provision of building and unit details.",
                "buildingNumberInfo": {
                  "buildingInfo": [
                    { "AM_ID": "50782408", "BLD_NUM_ID": "395208", "BLD_ID": "617914", "NUM": "1", "BUILDING_NAME": "EAGLES LANDING", "FLOOR": "GROUND" },
                    { "AM_ID": "50782408", "BLD_NUM_ID": "786154", "BLD_ID": "617914", "NUM": "12", "BUILDING_NAME": "EAGLES LANDING", "FLOOR": "GROUND" },
                    { "AM_ID": "50782408", "BLD_NUM_ID": "783682", "BLD_ID": "617914", "NUM": "18", "BUILDING_NAME": "EAGLES LANDING", "FLOOR": "GROUND" }
                  ]
                }
              },
              "ftthInfrastructure": {
                "ftthInfo": [
                  { "FTTH_Status": "Working", "fibreMaxSpeed": 500, "fibreMaxSpeedUnit": "Mbps",
                    "ftthProductInfo": [ { "ProductName": "Openserve Fibre Connect", "ProductCode": "OFC", "upstreamSpeed": "250 Mbps", "downstreamSpeed": "500 Mbps" } ] }
                ]
              }
            }
          }
        }
        """;

    private const string SingleUnitResponse = """
        {
          "errorCode": 0, "errorString": "OK", "message": "",
          "Results": { "payload": { "AddressInfo": {
            "AMID": "1000497",
            "buildingNumberInfo": { "buildingInfo": [ { "AM_ID": "1000497", "BLD_NUM_ID": "42", "NUM": "3F" } ] }
          }, "ftthInfrastructure": { "ftthInfo": [ { "FTTH_Status": "Working", "fibreMaxSpeed": 200, "fibreMaxSpeedUnit": "Mbps" } ] } } }
        }
        """;

    private const string NonMduResponse = """
        { "errorCode": 0, "errorString": "OK", "message": "",
          "Results": { "payload": { "AddressInfo": { "AMID": "1000497" },
          "ftthInfrastructure": { "ftthInfo": [ { "FTTH_Status": "Working", "fibreMaxSpeed": 100, "fibreMaxSpeedUnit": "Mbps" } ] } } } }
        """;

    [Fact]
    public async Task QualifyAsync_ByLatLon_BuildsDocumentedEndpointAndQuery()
    {
        var (client, handler) = Build(NonMduResponse);

        await client.QualifyAsync(new OpenserveQualificationQuery { Latitude = -26.09595m, Longitude = 27.927632m, BuildingInfo = true });

        Assert.NotNull(handler.LastRequestUri);
        Assert.StartsWith("https://testapitrx.openserve.co.za/ws-ispcode/productqualification?", handler.LastRequestUri);
        Assert.Contains("LAT=-26.09595", handler.LastRequestUri);
        Assert.Contains("LON=27.927632", handler.LastRequestUri);
        Assert.Contains("BuildingInfo=Y", handler.LastRequestUri);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.True(handler.LastHeaders!.Contains("api_key"));
    }

    [Fact]
    public async Task QualifyAsync_ByAmid_BuildsDocumentedQuery()
    {
        var (client, handler) = Build(NonMduResponse);

        await client.QualifyAsync(new OpenserveQualificationQuery { Amid = "50782408", BuildingInfo = true });

        Assert.Contains("AMID=50782408", handler.LastRequestUri);
    }

    [Fact]
    public async Task QualifyAsync_ExtractsAmid_FromNestedResultsPayload()
    {
        var (client, _) = Build(NonMduResponse);

        var result = await client.QualifyAsync(new OpenserveQualificationQuery { Latitude = -26m, Longitude = 27m });

        Assert.True(result.IsSuccess);
        Assert.Equal("1000497", result.Outcome!.Amid);
        Assert.Equal("Working", result.Outcome.FtthStatus);
        Assert.Equal(100m, result.Outcome.FibreMaxSpeed);
    }

    [Fact]
    public async Task QualifyAsync_SingleBuildingMatch_ExtractsBuildingNumId()
    {
        var (client, _) = Build(SingleUnitResponse);

        var result = await client.QualifyAsync(new OpenserveQualificationQuery { Latitude = -26m, Longitude = 27m });

        Assert.Equal("1000497", result.Outcome!.Amid);
        Assert.Equal("42", result.Outcome.BuildingNumId);
        Assert.Equal(1, result.Outcome.BuildingMatchCount);
    }

    [Fact]
    public async Task QualifyAsync_MultipleBuildingMatches_DoesNotAutoSelectBuildingNumId()
    {
        var (client, _) = Build(MduResponse);

        var result = await client.QualifyAsync(new OpenserveQualificationQuery { Amid = "50782408" });

        Assert.Equal("50782408", result.Outcome!.Amid);
        Assert.Null(result.Outcome.BuildingNumId); // ambiguous — 3 units, cannot guess
        Assert.Equal(3, result.Outcome.BuildingMatchCount);
    }

    [Fact]
    public async Task QualifyAsync_Mdu_ReturnsEveryBuildingRowVerbatim()
    {
        var (client, _) = Build(MduResponse);

        var result = await client.QualifyAsync(new OpenserveQualificationQuery { Amid = "50782408", BuildingInfo = true });

        var buildings = result.Outcome!.Buildings!;
        Assert.Equal(3, buildings.Count);
        var unit12 = buildings.Single(b => b.Num == "12");
        Assert.Equal("786154", unit12.BldNumId);
        Assert.Equal("617914", unit12.BldId);
        Assert.Equal("EAGLES LANDING", unit12.BuildingName);
        Assert.Equal("GROUND", unit12.Floor);
        Assert.Equal("50782408", unit12.AmId);
    }

    [Fact]
    public async Task QualifyAsync_ErrorCodeNonZero_ReturnsFailure()
    {
        var (client, _) = Build("""{"errorCode": -1, "errorString": "ERROR", "message": "No coverage found for this location."}""");

        var result = await client.QualifyAsync(new OpenserveQualificationQuery { Latitude = -26m, Longitude = 27m });

        Assert.False(result.IsSuccess);
    }

    // ─── FORCEVERIFY=Y (address verification) ───────────────────────

    [Fact]
    public async Task QualifyAsync_ForceVerify_SendsTheStagingQuery_AndReadsAddressVerify_WithoutChoosingACandidate()
    {
        var (client, handler) = Build(OpenserveEvidenceFixtures.UatAddressVerifyResponse);

        var result = await client.QualifyAsync(new OpenserveQualificationQuery { Latitude = -25.866217m, Longitude = 28.106903m, BuildingInfo = true, ForceVerify = true });

        Assert.Equal("https://testapitrx.openserve.co.za/ws-ispcode/productqualification?LAT=-25.866217&LON=28.106903&BuildingInfo=Y&FORCEVERIFY=Y", handler.LastRequestUri);
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Null(result.Outcome!.Amid); // nothing chosen — the closest record is NOT the answer
        var facts = result.Outcome.Facts!;
        Assert.True(facts.AddressVerifyReturned);
        Assert.Collection(facts.AddressCandidates!,
            c =>
            {
                Assert.Equal("52782141", c.Amid);
                Assert.Equal(0m, c.DistanceMeters);
                Assert.Equal("8 PALMAS ST MONAVONI X 6 CENTURION", c.Address);
                Assert.Equal(-25.866217m, c.Latitude);
                Assert.Equal(28.106903m, c.Longitude);
                Assert.Equal(".00 m", c.DistanceText);
            },
            c =>
            {
                Assert.Equal("80573005", c.Amid);
                Assert.Equal(26.7342855017672m, c.DistanceMeters);
                Assert.Equal("11A DE OVALLE BLV MONAVONI X 6 CENTURION", c.Address);
                Assert.Equal("26.73 m", c.DistanceText);
            },
            c => Assert.Equal("52782142", c.Amid));
        Assert.Empty(facts.Ftth); // a verification answer carries no Fibre facts at all
        Assert.Contains("REDACTED", result.RequestHeadersJson);
        Assert.DoesNotContain("test-key", result.RequestHeadersJson);
    }

    [Fact]
    public async Task QualifyAsync_ByAmid_NeverSendsForceVerify()
    {
        var (client, handler) = Build(NonMduResponse);

        await client.QualifyAsync(new OpenserveQualificationQuery { Amid = "52782141", BuildingInfo = true, ForceVerify = true });

        Assert.Equal("https://testapitrx.openserve.co.za/ws-ispcode/productqualification?AMID=52782141&BuildingInfo=Y", handler.LastRequestUri);
    }

    [Fact]
    public async Task QualifyAsync_ForceVerify_EmptyAddressVerify_IsAnAnswerWithNoCandidates()
    {
        var (client, _) = Build("""{"address":"","LAT":"-25.1","LON":"28.1","AddressVerify":[]}""");

        var result = await client.QualifyAsync(new OpenserveQualificationQuery { Latitude = -25.1m, Longitude = 28.1m, ForceVerify = true });

        Assert.True(result.IsSuccess);
        Assert.True(result.Outcome!.Facts!.AddressVerifyReturned);
        Assert.Empty(result.Outcome.Facts.AddressCandidates!);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public string? LastRequestUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public System.Net.Http.Headers.HttpRequestHeaders? LastHeaders { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            LastMethod = request.Method;
            LastHeaders = request.Headers;
            return await _respond(request);
        }
    }
}
