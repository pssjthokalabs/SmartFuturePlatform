using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Openserve;
using SmartFuture.Infrastructure.Openserve;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Contract tests against Openserve's provisioned Postman collection
// "Fulfilment API Collection (Broadband)" v02 — exact paths, per-operation
// headers and payload shapes, using Smart Future's supplied STAGING
// configuration values (non-secret). The api_key here is a fake. Uses a
// fake HttpMessageHandler so no real Openserve HTTP call ever leaves the
// machine — same pattern as PaystackVerificationServiceTests.
public class OpenserveApiClientTests
{
    // Supplied staging values (Postman variable → setting). Casing matters:
    // isp_tag "ws-marut" ≠ ISPID "WS MARUT".
    private const string HostUrl = "https://stapitrx.openserve.co.za";
    private const string IspTag = "ws-marut";
    private const string IspId = "WS MARUT";
    private const string SenderId = "SMARTFUTURE";
    private const string ReplyToAddress = "https://stapitrx.openserve.co.za/ws-marut/productordercallback";
    private const string FakeApiKey = "fake-staging-api-key-7f3a9c";

    private static OpenserveFulfilmentSettings Settings() => new()
    {
        Enabled = true,
        BaseUrl = HostUrl,
        ApiKey = FakeApiKey,
        WsIspCode = IspTag,
        IspIdentifier = IspId,
        ReplyToAddress = ReplyToAddress,
        SenderId = SenderId
    };

    // A fresh HttpResponseMessage/StringContent is built PER CALL —
    // HttpClient/OpenserveApiClient dispose both the response and its
    // own request+content after each SendAsync, so returning the same
    // instance twice throws ObjectDisposedException on the second call.
    private static (OpenserveApiClient client, RecordingHandler handler) Build(string body, HttpStatusCode status = HttpStatusCode.OK, OpenserveFulfilmentSettings? settings = null, string mediaType = "application/json")
    {
        var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) }));
        var httpClient = new HttpClient(handler);
        var configProvider = new Mock<IOpenserveRuntimeConfigProvider>();
        configProvider.Setup(m => m.Current).Returns(settings ?? Settings());
        var client = new OpenserveApiClient(httpClient, configProvider.Object, NullLogger<OpenserveApiClient>.Instance);
        return (client, handler);
    }

    private static OpenserveCreateOrderCommand ValidCommand() => new()
    {
        ExternalReferenceNumber = "SF-ABC123",
        OpenserveProductName = "Openserve Fibre Connect",
        Sku = "OFC",
        Capacity = "100",
        CapacityUom = "Mbps",
        SubscriberReferenceNumber = "SF-NET-20260909-XYZ123",
        SubscriberContactName = "Jane Doe",
        SubscriberContactPhone = "0821234567",
        IspIdentifier = IspId,
        Street1 = "251 Zone 5f Seshego",
        Suburb = "Helderberg Village",
        City = "Polokwane",
        Region = "Limpopo",
        Latitude = "-34.049384",
        Longitude = "18.81537",
        Amid = "46093228"
    };

    // Postman "UC 1: Create New Order" → saved example response, verbatim.
    private const string PostmanAck = """{"ErrorResult":{"code":"0","reason":"OS-ESB-ACK","message":"Order received for processing. Order Id = 1742148. State = Validated"}}""";

    // Postman UC 3/4/5 saved 400 example, verbatim.
    private const string Postman400 = """{"ErrorResult":{"code":"1","reason":"E84119","message":"Service Number B127700484 does not exist in the system."}}""";

    // Postman "Query Order Details" → "GetOrderDetails example" response (trimmed to the fields we read).
    private const string PostmanGetOrderDetails = """
        {
          "@type": "Cancel Market Offer",
          "orderCharacteristic": [
            { "name": "OrderName", "value": "CM319193" },
            { "name": "Requested Date", "value": "2023-04-22T09:48:05" },
            { "name": "OrderType", "value": "Cancel Market Offer" }
          ],
          "channel": [ { "name": "Incoming Phone", "id": 2 } ],
          "href": "bdolsvwsapp1:/trerest_in/api/v1/productOrder/319193",
          "id": "319193",
          "state": "Accepted",
          "productOrderItem": [
            { "product": {
                "productOffering": { "name": "Openserve Copper Connect", "id": "1000492" },
                "realizingService": [
                  { "name": "B999999999", "op": "remove", "id": 2500001, "@type": "Broadband",
                    "serviceCharacteristic": [ { "name": "ISP Identifier", "value": "WS TEST" } ] }
                ] } }
          ],
          "orderDate": "2023-04-22T09:48:05",
          "case": { "name": "C358748", "id": 358748, "status": "Closed" }
        }
        """;

    private const string QualificationOk = """{"errorCode":0,"errorString":"OK","message":"","Results":{"payload":{"AddressInfo":{"AMID":"50782408"}}}}""";

    // ─── Create New Order (UC 1) ─────────────────────────────────────

    [Fact]
    public async Task CreateOrderAsync_PostsToPostmanPath_LowercaseProductorder_UnderIspTag()
    {
        var (client, handler) = Build(PostmanAck);

        await client.CreateOrderAsync(ValidCommand());

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("https://stapitrx.openserve.co.za/ws-marut/productorder", handler.LastRequestUri);
    }

    [Fact]
    public async Task CreateOrderAsync_SendsPostmanOrderingHeaders_FromLocationIsIspId()
    {
        var (client, handler) = Build(PostmanAck);

        await client.CreateOrderAsync(ValidCommand());

        var h = handler.LastHeaders!;
        Assert.True(Guid.TryParse(h["MessageID"], out _));
        Assert.Equal(IspId, h["FromLocation"]);            // {{ISPID}} — exact, with space and caps
        Assert.Equal(SenderId, h["SenderID"]);
        Assert.Equal(ReplyToAddress, h["ReplyToAddress"]); // Openserve-provided value, verbatim
        Assert.Equal(FakeApiKey, h["api_key"]);
        Assert.Contains("application/json", handler.LastAccept);
        Assert.StartsWith("application/json", handler.LastContentType);
    }

    [Fact]
    public async Task CreateOrderAsync_ReplyToAddress_IsConfiguredOpenserveValue_NotSmartFutureCallback()
    {
        var (client, handler) = Build(PostmanAck);

        await client.CreateOrderAsync(ValidCommand());

        Assert.Equal("https://stapitrx.openserve.co.za/ws-marut/productordercallback", handler.LastHeaders!["ReplyToAddress"]);
        Assert.DoesNotContain("/api/openserve/callback", handler.LastHeaders["ReplyToAddress"]);
    }

    [Fact]
    public async Task CreateOrderAsync_GeneratesFreshMessageId_OnEveryInvocation()
    {
        var (client, handler) = Build(PostmanAck);

        await client.CreateOrderAsync(ValidCommand());
        var first = handler.LastHeaders!["MessageID"];
        await client.CreateOrderAsync(ValidCommand());
        var second = handler.LastHeaders!["MessageID"];

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task CreateOrderAsync_BuildsPayloadMatchingPostmanUc1Shape()
    {
        var (client, handler) = Build(PostmanAck);

        await client.CreateOrderAsync(ValidCommand());

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal("Sales Order", root.GetProperty("@type").GetString());
        Assert.True(root.TryGetProperty("requestedStartDate", out _));

        var item = root.GetProperty("productOrderItem")[0];
        Assert.Equal("add", item.GetProperty("action").GetString());

        var product = item.GetProperty("product");
        Assert.Equal("Openserve Fibre Connect", product.GetProperty("productOffering").GetProperty("name").GetString());

        var characteristics = product.GetProperty("productCharacteristic").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("value").GetString());
        Assert.Equal("SF-ABC123", characteristics["External Reference Number"]);
        Assert.Equal("OFC", characteristics["SKU"]);

        var relationship = product.GetProperty("productRelationship")[0];
        Assert.Equal("childOffer", relationship.GetProperty("relationshipType").GetString());
        Assert.Equal(JsonValueKind.Object, relationship.GetProperty("product").ValueKind);

        var realizingService = product.GetProperty("realizingService")[0];
        var place = realizingService.GetProperty("place")[0];
        Assert.Equal("A", place.GetProperty("@type").GetString());
        Assert.Equal("South Africa", place.GetProperty("country").GetString());
        Assert.Equal("46093228", place.GetProperty("amid").GetString());
        Assert.Equal("251 Zone 5f Seshego", place.GetProperty("street1").GetString());
        Assert.Equal("Helderberg Village", place.GetProperty("suburb").GetString());
        Assert.Equal("Polokwane", place.GetProperty("city").GetString());
        Assert.Equal("Limpopo", place.GetProperty("region").GetString());
        Assert.Equal("-34.049384", place.GetProperty("latitude").GetString());
        Assert.Equal("18.81537", place.GetProperty("longitude").GetString());

        var serviceChars = realizingService.GetProperty("serviceCharacteristic").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("value").GetString());
        Assert.Equal("100", serviceChars["Capacity"]);
        Assert.Equal("Mbps", serviceChars["Capacity UOM"]);
        Assert.Equal("SF-NET-20260909-XYZ123", serviceChars["Subscriber Reference Number"]);
        Assert.Equal("Jane Doe", serviceChars["Subscriber Contact Name"]);
        Assert.Equal("0821234567", serviceChars["Subscriber Contact Phone"]);
        Assert.Equal("WS MARUT", serviceChars["ISP Identifier"]); // exact ISPID preserved
    }

    [Fact]
    public async Task CreateOrderAsync_MduPlace_CarriesQualificationBuildingValuesVerbatim()
    {
        var (client, handler) = Build(PostmanAck);
        var command = ValidCommand();
        var mdu = new OpenserveCreateOrderCommand
        {
            ExternalReferenceNumber = command.ExternalReferenceNumber, OpenserveProductName = command.OpenserveProductName, Sku = command.Sku,
            Capacity = command.Capacity, CapacityUom = command.CapacityUom, IspIdentifier = command.IspIdentifier, Street1 = command.Street1, Amid = "50782408",
            BuildingName = "EAGLES LANDING SHOPPING CENTRE", Floor = "GROUND", Unit = "12", BuildingNumId = "786154"
        };

        await client.CreateOrderAsync(mdu);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var place = doc.RootElement.GetProperty("productOrderItem")[0].GetProperty("product").GetProperty("realizingService")[0].GetProperty("place")[0];
        Assert.Equal("EAGLES LANDING SHOPPING CENTRE", place.GetProperty("buildingName").GetString()); // BUILDING_NAME
        Assert.Equal("GROUND", place.GetProperty("floor").GetString());                               // FLOOR
        Assert.Equal("12", place.GetProperty("unit").GetString());                                    // NUM
        Assert.Equal("786154", place.GetProperty("buildingNumId").GetString());                       // BLD_NUM_ID
    }

    [Fact]
    public async Task CreateOrderAsync_ParsesOrderIdAndState_FromPostmanAckMessage()
    {
        var (client, _) = Build(PostmanAck);

        var result = await client.CreateOrderAsync(ValidCommand());

        Assert.True(result.IsSuccess);
        Assert.Equal("1742148", result.Outcome!.ParsedOrderId);
        Assert.Equal("Validated", result.Outcome.ParsedState);
    }

    [Fact]
    public async Task CreateOrderAsync_ParsesOrderId_FromPdfAckWording_Too()
    {
        var (client, _) = Build("""{"ErrorResult":{"code":"0","reason":"OS-ESB-ACK","message":"Order Received for Processing. Order ID = 302114. State = Validated."}}""");

        var result = await client.CreateOrderAsync(ValidCommand());

        Assert.Equal("302114", result.Outcome!.ParsedOrderId);
        Assert.Equal("Validated", result.Outcome.ParsedState);
    }

    [Fact]
    public async Task CreateOrderAsync_BusinessRejection_ReturnsFailureWithOpenserveErrorCode()
    {
        var (client, _) = Build("""{"ErrorResult":{"code":"1","reason":"GEN-45994","message":"BSS ERROR: There is no configuration under DA table."}}""");

        var result = await client.CreateOrderAsync(ValidCommand());

        Assert.False(result.IsSuccess);
        Assert.Equal("GEN-45994", result.ErrorCode);
    }

    [Fact]
    public async Task Http400WithPostmanErrorResult_SurfacesOpenserveReasonAndMessage()
    {
        var (client, _) = Build(Postman400, HttpStatusCode.BadRequest);

        var result = await client.CreateOrderAsync(ValidCommand());

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.HttpStatusCode);
        Assert.Equal("E84119", result.ErrorCode);
        Assert.Contains("does not exist in the system", result.ErrorMessage);
        Assert.False(result.ReachedOpenserve);
    }

    [Fact]
    public async Task NonJsonErrorBody_SurfacesPlainTextSnippet()
    {
        var (client, _) = Build("<h1>Developer Inactive</h1>", HttpStatusCode.Forbidden, mediaType: "text/html");

        var result = await client.QualifyAsync(new OpenserveQualificationQuery { Amid = "50782408" });

        Assert.False(result.IsSuccess);
        Assert.Equal(403, result.HttpStatusCode);
        Assert.Contains("Developer Inactive", result.ErrorMessage);
    }

    [Fact]
    public async Task CreateOrderAsync_HttpFailure_ReturnsFailure_NotException()
    {
        var (client, _) = Build("Internal error", HttpStatusCode.InternalServerError);

        var result = await client.CreateOrderAsync(ValidCommand());

        Assert.False(result.IsSuccess);
        Assert.Equal(500, result.HttpStatusCode);
    }

    // ─── Query Order Details ─────────────────────────────────────────

    [Fact]
    public async Task GetOrderAsync_UsesPostmanQueryOrderDetailsPath_GetProductOrder()
    {
        var (client, handler) = Build(PostmanGetOrderDetails);

        await client.GetOrderAsync("1742148");

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("https://stapitrx.openserve.co.za/ws-marut/getproductorder/1742148", handler.LastRequestUri);
        Assert.DoesNotContain("/productorder/", handler.LastRequestUri);
        Assert.Null(handler.LastRequestBody);
    }

    [Fact]
    public async Task GetOrderAsync_SendsPostmanOrderingHeaders()
    {
        var (client, handler) = Build(PostmanGetOrderDetails);

        await client.GetOrderAsync("1742148");

        var h = handler.LastHeaders!;
        Assert.True(Guid.TryParse(h["MessageID"], out _));
        Assert.Equal(IspId, h["FromLocation"]);
        Assert.Equal(SenderId, h["SenderID"]);
        Assert.Equal(ReplyToAddress, h["ReplyToAddress"]);
        Assert.Equal(FakeApiKey, h["api_key"]);
    }

    [Fact]
    public async Task GetOrderAsync_ParsesPostmanExampleResponse()
    {
        var (client, _) = Build(PostmanGetOrderDetails);

        var result = await client.GetOrderAsync("319193");

        Assert.True(result.IsSuccess);
        Assert.Equal("319193", result.Outcome!.Id);
        Assert.Equal("Accepted", result.Outcome.State);
        Assert.Equal("CM319193", result.Outcome.OrderName);
        Assert.Equal("Cancel Market Offer", result.Outcome.OrderType);
        Assert.Equal("B999999999", result.Outcome.CircuitNumber);
        Assert.Equal("2023-04-22T09:48:05", result.Outcome.OrderDate);
    }

    [Fact]
    public async Task GetOrderAsync_200WithErrorResultEnvelope_IsFailure()
    {
        var (client, _) = Build("""{"ErrorResult":{"code":"1","reason":"E10001","message":"Order 999 not found."}}""");

        var result = await client.GetOrderAsync("999");

        Assert.False(result.IsSuccess);
        Assert.Equal("E10001", result.ErrorCode);
        Assert.Contains("not found", result.ErrorMessage);
    }

    // ─── Cancel Inflight Order ───────────────────────────────────────

    [Fact]
    public async Task CancelOrderAsync_PostsToPostmanPath_WithProductOrderIdBody_AndOrderingHeaders()
    {
        var (client, handler) = Build("""{"Result":{"ResultCode":"0"},"Payload":{"id":"304098","state":"Cancelled","effectiveCancellationDate":"2023-06-07T09:24:07"}}""");

        var result = await client.CancelOrderAsync("304098");

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("https://stapitrx.openserve.co.za/ws-marut/cancelproductorder", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("304098", doc.RootElement.GetProperty("productOrder").GetProperty("id").GetString());
        Assert.Equal(IspId, handler.LastHeaders!["FromLocation"]);
        Assert.Equal(ReplyToAddress, handler.LastHeaders["ReplyToAddress"]);
        Assert.True(result.IsSuccess);
        Assert.Equal("Cancelled", result.Outcome!.State);
    }

    [Fact]
    public async Task CancelOrderAsync_AcceptsNumericResultCode()
    {
        var (client, _) = Build("""{"Result":{"ResultCode":0,"ResultMsgCode":"OS-ESB-ACK-001","ResultMsg":"Message Accepted for Processing"},"Payload":{"id":"304098","state":"Pending Cancellation"}}""");

        var result = await client.CancelOrderAsync("304098");

        Assert.True(result.IsSuccess);
        Assert.Equal("Pending Cancellation", result.Outcome!.State);
    }

    // ─── Product Qualification ───────────────────────────────────────

    [Fact]
    public async Task QualifyAsync_ByAmid_UsesExactPostmanPathAndQuery()
    {
        var (client, handler) = Build(QualificationOk);

        await client.QualifyAsync(new OpenserveQualificationQuery { Amid = "50782408", BuildingInfo = true });

        Assert.Equal(HttpMethod.Get, handler.LastMethod);
        Assert.Equal("https://stapitrx.openserve.co.za/ws-marut/productqualification?AMID=50782408&BuildingInfo=Y", handler.LastRequestUri);
    }

    [Fact]
    public async Task QualifyAsync_ByLatLon_UsesExactPostmanPathAndQuery()
    {
        var (client, handler) = Build(QualificationOk);

        await client.QualifyAsync(new OpenserveQualificationQuery { Latitude = -26.09595m, Longitude = 27.927632m, BuildingInfo = true });

        Assert.Equal("https://stapitrx.openserve.co.za/ws-marut/productqualification?LAT=-26.09595&LON=27.927632&BuildingInfo=Y", handler.LastRequestUri);
    }

    [Fact]
    public async Task QualifyAsync_AmidAndCoordinates_SendsAmidOnly()
    {
        var (client, handler) = Build(QualificationOk);

        await client.QualifyAsync(new OpenserveQualificationQuery { Amid = "50782408", Latitude = -26.09595m, Longitude = 27.927632m });

        Assert.Contains("AMID=50782408", handler.LastRequestUri);
        Assert.DoesNotContain("LAT=", handler.LastRequestUri);
        Assert.DoesNotContain("LON=", handler.LastRequestUri);
    }

    [Fact]
    public async Task QualifyAsync_SendsPostmanQualificationHeaders_FromLocationIsIspTag_NoReplyToAddress()
    {
        var (client, handler) = Build(QualificationOk);

        await client.QualifyAsync(new OpenserveQualificationQuery { Amid = "50782408" });

        var h = handler.LastHeaders!;
        Assert.True(Guid.TryParse(h["MessageID"], out _));
        Assert.Equal(IspTag, h["FromLocation"]);  // {{isp_tag}} — NOT the ISPID
        Assert.Equal(SenderId, h["SenderID"]);
        Assert.Equal(FakeApiKey, h["api_key"]);
        Assert.False(h.ContainsKey("ReplyToAddress"));
    }

    // ─── Cross-cutting ───────────────────────────────────────────────

    [Fact]
    public async Task FromLocation_DiffersPerOperation_ExactlyAsPostmanDefinesIt()
    {
        var (client, handler) = Build(QualificationOk);
        await client.QualifyAsync(new OpenserveQualificationQuery { Amid = "50782408" });
        var qualificationFromLocation = handler.LastHeaders!["FromLocation"];

        var (orderClient, orderHandler) = Build(PostmanAck);
        await orderClient.CreateOrderAsync(ValidCommand());
        var orderingFromLocation = orderHandler.LastHeaders!["FromLocation"];

        Assert.Equal("ws-marut", qualificationFromLocation);
        Assert.Equal("WS MARUT", orderingFromLocation);
        Assert.NotEqual(qualificationFromLocation, orderingFromLocation);
    }

    [Fact]
    public async Task MessageId_IsUniquePerOutboundRequest_AcrossAllOperations()
    {
        var handler = new RecordingHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var body = path.Contains("productqualification") ? QualificationOk
                : path.Contains("getproductorder") ? PostmanGetOrderDetails
                : path.Contains("cancelproductorder") ? """{"Result":{"ResultCode":"0"},"Payload":{"id":"1","state":"Cancelled"}}"""
                : PostmanAck;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        });
        var configProvider = new Mock<IOpenserveRuntimeConfigProvider>();
        configProvider.Setup(m => m.Current).Returns(Settings());
        var client = new OpenserveApiClient(new HttpClient(handler), configProvider.Object, NullLogger<OpenserveApiClient>.Instance);

        var ids = new List<string>
        {
            (await client.QualifyAsync(new OpenserveQualificationQuery { Amid = "50782408" })).MessageId,
            (await client.CreateOrderAsync(ValidCommand())).MessageId,
            (await client.GetOrderAsync("1742148")).MessageId,
            (await client.GetOrderAsync("1742148")).MessageId,
            (await client.CancelOrderAsync("1742148")).MessageId
        };

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(ids, handler.SentMessageIds); // the id reported for logging is the one actually sent
    }

    [Fact]
    public async Task SanitizedRequestHeaders_RedactApiKey_ButKeepEveryOtherHeader()
    {
        var (client, _) = Build(PostmanAck);

        var result = await client.CreateOrderAsync(ValidCommand());

        Assert.NotNull(result.RequestHeadersJson);
        Assert.DoesNotContain(FakeApiKey, result.RequestHeadersJson);
        var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(result.RequestHeadersJson!)!;
        Assert.Equal(OpenserveHeaderRedaction.RedactedValue, headers["api_key"]);
        Assert.Equal(IspId, headers["FromLocation"]);
        Assert.Equal(SenderId, headers["SenderID"]);
        Assert.Equal(ReplyToAddress, headers["ReplyToAddress"]);
        Assert.Equal(result.MessageId, headers["MessageID"]);
    }

    [Fact]
    public async Task ApiKey_NeverAppearsInAnyResultField_OnSuccessOrFailure()
    {
        foreach (var (body, status) in new[] { (PostmanAck, HttpStatusCode.OK), (Postman400, HttpStatusCode.BadRequest), ("boom", HttpStatusCode.InternalServerError) })
        {
            var (client, _) = Build(body, status);
            var result = await client.CreateOrderAsync(ValidCommand());
            var serialized = JsonSerializer.Serialize(result);
            Assert.DoesNotContain(FakeApiKey, serialized);
        }
    }

    [Fact]
    public async Task TransportFailure_StillCarriesSanitizedHeaders()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("No such host is known."));
        var configProvider = new Mock<IOpenserveRuntimeConfigProvider>();
        configProvider.Setup(m => m.Current).Returns(Settings());
        var client = new OpenserveApiClient(new HttpClient(handler), configProvider.Object, NullLogger<OpenserveApiClient>.Instance);

        var result = await client.GetOrderAsync("1742148");

        Assert.False(result.IsSuccess);
        Assert.Equal("TRANSPORT_ERROR", result.ErrorCode);
        Assert.NotNull(result.RequestHeadersJson);
        Assert.DoesNotContain(FakeApiKey, result.RequestHeadersJson);
    }

    [Fact]
    public async Task BaseUrlTrailingSlash_AndIspTagSlashes_DoNotProduceDoubleSlashes()
    {
        var settings = Settings();
        settings.BaseUrl = HostUrl + "/";
        settings.WsIspCode = "/ws-marut/";
        var (client, handler) = Build(PostmanGetOrderDetails, settings: settings);

        await client.GetOrderAsync("1742148");

        Assert.Equal("https://stapitrx.openserve.co.za/ws-marut/getproductorder/1742148", handler.LastRequestUri);
    }

    // Captures everything a test might need to assert BEFORE the
    // client's own `using` blocks dispose the request/response —
    // exposing HttpRequestMessage itself post-call is a trap, since
    // OpenserveApiClient wraps it in `using var httpRequest = ...`.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

        public string? LastRequestUri { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Dictionary<string, string>? LastHeaders { get; private set; }
        public string LastAccept { get; private set; } = string.Empty;
        public string? LastContentType { get; private set; }
        public string? LastRequestBody { get; private set; }
        public List<string> SentMessageIds { get; } = new();

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            LastMethod = request.Method;
            LastHeaders = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value));
            LastAccept = request.Headers.Accept.ToString();
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (LastHeaders.TryGetValue("MessageID", out var messageId)) SentMessageIds.Add(messageId);
            return await _respond(request);
        }
    }
}
