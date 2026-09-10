using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SmartFuture.Application.Openserve;
using SmartFuture.Infrastructure.Openserve;
using Xunit;

namespace SmartFuture.Tests.Openserve;

// Brief §Priority-1/TEST REQUIREMENTS: exact endpoint construction,
// required headers, MessageID freshness per invocation. Uses a fake
// HttpMessageHandler so no real Openserve HTTP call ever leaves the
// machine — same pattern as PaystackVerificationServiceTests.
public class OpenserveApiClientTests
{
    private static OpenserveFulfilmentSettings Settings() => new()
    {
        Enabled = true,
        BaseUrl = "https://testapitrx.openserve.co.za",
        ApiKey = "test-api-key-123",
        WsIspCode = "ws-ispcode",
        IspIdentifier = "WS SMARTFUTURE",
        ReplyToAddress = "https://api.smartfuture.co.za/api/openserve/callback",
        SenderId = "SmartFuture"
    };

    // A fresh HttpResponseMessage/StringContent is built PER CALL —
    // HttpClient/OpenserveApiClient dispose both the response and its
    // own request+content after each SendAsync, so returning the same
    // instance twice throws ObjectDisposedException on the second call.
    private static (OpenserveApiClient client, RecordingHandler handler) Build(string json, HttpStatusCode status = HttpStatusCode.OK, OpenserveFulfilmentSettings? settings = null)
    {
        var handler = new RecordingHandler(_ => Task.FromResult(
            new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        var httpClient = new HttpClient(handler);
        var monitor = new Mock<Microsoft.Extensions.Options.IOptionsMonitor<OpenserveFulfilmentSettings>>();
        monitor.Setup(m => m.CurrentValue).Returns(settings ?? Settings());
        var client = new OpenserveApiClient(httpClient, monitor.Object, NullLogger<OpenserveApiClient>.Instance);
        return (client, handler);
    }

    private static OpenserveCreateOrderCommand ValidCommand() => new()
    {
        ExternalReferenceNumber = "SF-ABC123",
        OpenserveProductName = "Openserve Fibre Connect",
        Sku = "OFC",
        Capacity = "75",
        CapacityUom = "Mbps",
        SubscriberReferenceNumber = "SF-NET-20260909-XYZ123",
        SubscriberContactName = "Jane Doe",
        SubscriberContactPhone = "0821234567",
        IspIdentifier = "WS SMARTFUTURE",
        Street1 = "61 Oak Ave",
        Suburb = "Highveld Techno Park",
        City = "Centurion",
        Region = "Gauteng",
        Amid = "1000497"
    };

    private const string AckSuccess = """{"ErrorResult":{"code":"0","reason":"OS-ESB-ACK","message":"Order Received for Processing. Order ID = 302114. State = Validated."}}""";

    [Fact]
    public async Task CreateOrderAsync_PostsToDocumentedEndpoint()
    {
        var (client, handler) = Build(AckSuccess);

        await client.CreateOrderAsync(ValidCommand());

        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("https://testapitrx.openserve.co.za/ws-ispcode/productOrder", handler.LastRequestUri);
    }

    [Fact]
    public async Task CreateOrderAsync_SendsRequiredHeaders()
    {
        var (client, handler) = Build(AckSuccess);

        await client.CreateOrderAsync(ValidCommand());

        var headers = handler.LastHeaders!;
        Assert.True(headers.Contains("api_key"));
        Assert.Equal("test-api-key-123", headers.GetValues("api_key").Single());
        Assert.True(headers.Contains("MessageID"));
        Assert.True(headers.Contains("ReplyToAddress"));
        Assert.Equal("https://api.smartfuture.co.za/api/openserve/callback", headers.GetValues("ReplyToAddress").Single());
        Assert.True(headers.Contains("SenderID"));
        Assert.Contains("application/json", headers.Accept.ToString());
        Assert.StartsWith("application/json", handler.LastContentType);
    }

    [Fact]
    public async Task CreateOrderAsync_GeneratesFreshMessageId_OnEveryInvocation()
    {
        var (client, handler) = Build(AckSuccess);

        await client.CreateOrderAsync(ValidCommand());
        var firstMessageId = handler.LastHeaders!.GetValues("MessageID").Single();

        await client.CreateOrderAsync(ValidCommand());
        var secondMessageId = handler.LastHeaders!.GetValues("MessageID").Single();

        Assert.NotEqual(firstMessageId, secondMessageId);
        Assert.True(Guid.TryParse(firstMessageId, out _));
        Assert.True(Guid.TryParse(secondMessageId, out _));
    }

    [Fact]
    public async Task CreateOrderAsync_BuildsPayloadMatchingSpecShape()
    {
        var (client, handler) = Build(AckSuccess);

        await client.CreateOrderAsync(ValidCommand());

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;

        Assert.Equal("Sales Order", root.GetProperty("@type").GetString());
        var item = root.GetProperty("productOrderItem")[0];
        Assert.Equal("add", item.GetProperty("action").GetString());

        var product = item.GetProperty("product");
        Assert.Equal("Openserve Fibre Connect", product.GetProperty("productOffering").GetProperty("name").GetString());

        var characteristics = product.GetProperty("productCharacteristic").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("value").GetString());
        Assert.Equal("SF-ABC123", characteristics["External Reference Number"]);
        Assert.Equal("OFC", characteristics["SKU"]);

        var realizingService = product.GetProperty("realizingService")[0];
        var place = realizingService.GetProperty("place")[0];
        Assert.Equal("1000497", place.GetProperty("amid").GetString());
        Assert.Equal("South Africa", place.GetProperty("country").GetString());

        var serviceChars = realizingService.GetProperty("serviceCharacteristic").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("value").GetString());
        Assert.Equal("75", serviceChars["Capacity"]);
        Assert.Equal("Mbps", serviceChars["Capacity UOM"]);
        Assert.Equal("SF-NET-20260909-XYZ123", serviceChars["Subscriber Reference Number"]);
        Assert.Equal("WS SMARTFUTURE", serviceChars["ISP Identifier"]);
    }

    [Fact]
    public async Task CreateOrderAsync_ParsesOrderIdAndStateFromAckMessage()
    {
        var (client, _) = Build(AckSuccess);

        var result = await client.CreateOrderAsync(ValidCommand());

        Assert.True(result.IsSuccess);
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
    public async Task CreateOrderAsync_HttpFailure_ReturnsFailure_NotException()
    {
        var (client, _) = Build("Internal error", HttpStatusCode.InternalServerError);

        var result = await client.CreateOrderAsync(ValidCommand());

        Assert.False(result.IsSuccess);
        Assert.Equal(500, result.HttpStatusCode);
    }

    [Fact]
    public async Task GetOrderAsync_UsesDocumentedLowercasePathPattern()
    {
        var (client, handler) = Build("""{"id":"302050","state":"Accepted"}""");

        await client.GetOrderAsync("302050");

        Assert.Equal("https://testapitrx.openserve.co.za/ws-ispcode/productorder/302050", handler.LastRequestUri);
        Assert.Equal(HttpMethod.Get, handler.LastMethod);
    }

    [Fact]
    public async Task CancelOrderAsync_PostsToDocumentedEndpoint_WithOrderIdBody()
    {
        var (client, handler) = Build("""{"Result":{"ResultCode":"0"},"Payload":{"id":"304098","state":"Cancelled","effectiveCancellationDate":"2023-06-07T09:24:07"}}""");

        var result = await client.CancelOrderAsync("304098");

        Assert.Equal("https://testapitrx.openserve.co.za/ws-ispcode/cancelproductorder", handler.LastRequestUri);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("304098", doc.RootElement.GetProperty("productOrder").GetProperty("id").GetString());
        Assert.True(result.IsSuccess);
        Assert.Equal("Cancelled", result.Outcome!.State);
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
        public System.Net.Http.Headers.HttpRequestHeaders? LastHeaders { get; private set; }
        public string? LastContentType { get; private set; }
        public string? LastRequestBody { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            LastMethod = request.Method;
            LastHeaders = request.Headers;
            LastContentType = request.Content?.Headers.ContentType?.ToString();
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _respond(request);
        }
    }
}
