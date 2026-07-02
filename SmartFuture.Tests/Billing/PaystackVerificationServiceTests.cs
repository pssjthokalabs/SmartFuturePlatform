using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Payments.Paystack;

namespace SmartFuture.Tests.Billing;

// Phase-5 tests for PaystackVerificationService — the SmartFuture-side
// wrapper around Paystack's GET /transaction/verify/{ref} endpoint.
// Uses a fake HttpMessageHandler so no real Paystack HTTP call ever
// leaves the machine.
//
// The wrapper's contract:
//   • Requires Paystack to be configured (IsConfigured=true) — otherwise
//     returns a PROVIDER_NOT_CONFIGURED failure.
//   • Attaches "Authorization: Bearer {SecretKey}" on every call.
//   • Maps the JSON response into PaystackVerifyOutcome or a Failure
//     Result with a clear message — NEVER throws to callers.
//   • On transport failure / malformed JSON / non-2xx: returns
//     UPSTREAM_UNAVAILABLE failure.
public class PaystackVerificationServiceTests
{
    private const string ValidSecretKey = "sk_test_XXXXXXXXXXXXXXXXXXXXXXXX";
    private const string ValidPublicKey = "pk_test_XXXXXXXXXXXXXXXXXXXXXXXX";

    private static PaystackSettings ConfiguredSettings() => new()
    {
        Enabled = true,
        SecretKey = ValidSecretKey,
        PublicKey = ValidPublicKey,
        CallbackUrl = "https://uatapi.smartfuture.co.za/api/payments/paystack/callback",
        VerifyBaseUrl = "https://api.paystack.co/transaction/verify",
        Currency = "ZAR",
    };

    private static (PaystackVerificationService svc, RecordingHandler handler) Build(
        HttpResponseMessage response,
        PaystackSettings? settings = null)
    {
        var handler = new RecordingHandler(_ => Task.FromResult(response));
        return BuildWith(handler, settings);
    }

    private static (PaystackVerificationService svc, RecordingHandler handler) BuildWith(
        RecordingHandler handler, PaystackSettings? settings = null)
    {
        var client = new HttpClient(handler);
        var svc = new PaystackVerificationService(
            client,
            Options.Create(settings ?? ConfiguredSettings()),
            NullLogger<PaystackVerificationService>.Instance);
        return (svc, handler);
    }

    private static HttpResponseMessage OkJson(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage StatusJson(HttpStatusCode code, string json) =>
        new(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private const string SuccessBody =
        """
        {
          "status": true,
          "message": "Verification successful",
          "data": {
            "id": 302961,
            "status": "success",
            "reference": "TX-REF-1",
            "amount": 99900,
            "currency": "ZAR",
            "gateway_response": "Approved",
            "paid_at": "2026-08-15T12:34:56Z",
            "customer": { "email": "customer@example.com", "customer_code": "CUS_XYZ" },
            "authorization": {
              "authorization_code": "AUTH_ABC",
              "reusable": true,
              "signature": "SIG_XYZ",
              "channel": "card",
              "card_type": "visa",
              "last4": "4242",
              "exp_month": "12",
              "exp_year": "2030",
              "bank": "TEST BANK",
              "account_name": "Test Customer"
            }
          }
        }
        """;

    // ─── Success + mapping ─────────────────────────────────────────

    [Fact]
    public async Task PaystackVerificationService_SuccessResponse_MapsCorrectly()
    {
        var (svc, handler) = Build(OkJson(SuccessBody));

        var result = await svc.VerifyAsync("TX-REF-1");

        result.IsSuccess.Should().BeTrue(result.Message);
        result.Data!.Reference.Should().Be("TX-REF-1");
        result.Data.Status.Should().Be("success");
        result.Data.AmountSubunits.Should().Be(99_900);
        result.Data.Currency.Should().Be("ZAR");
        result.Data.CustomerEmail.Should().Be("customer@example.com");
        result.Data.PaidAtUtc.Should().Be(new DateTime(2026, 8, 15, 12, 34, 56, DateTimeKind.Utc));
        result.Data.ProviderTransactionId.Should().Be("302961");
        result.Data.Authorization.Should().NotBeNull();
        result.Data.Authorization!.Reusable.Should().BeTrue();
        result.Data.Authorization.AuthorizationCode.Should().Be("AUTH_ABC");

        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be(
            "https://api.paystack.co/transaction/verify/TX-REF-1");
    }

    // ─── Failure branches ─────────────────────────────────────────

    [Fact]
    public async Task PaystackVerificationService_FailedResponse_MapsCorrectly()
    {
        // Paystack returns HTTP 200 but with `status: false`. Wrapper
        // must translate to a Failure Result with the message Paystack
        // provided so the caller can log it.
        const string body =
            """
            { "status": false, "message": "Transaction not found", "data": null }
            """;
        var (svc, _) = Build(OkJson(body));

        var result = await svc.VerifyAsync("TX-MISSING");

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("Transaction not found");
    }

    [Fact]
    public async Task PaystackVerificationService_InvalidJson_ReturnsSafeFailure()
    {
        var (svc, _) = Build(OkJson("this-is-not-json"));

        var result = await svc.VerifyAsync("TX-REF-1");

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PaystackVerificationService_HttpError_ReturnsSafeFailure()
    {
        var (svc, _) = Build(StatusJson(HttpStatusCode.ServiceUnavailable,
            """{ "status": false, "message": "Service temporarily unavailable" }"""));

        var result = await svc.VerifyAsync("TX-REF-1");

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("Service temporarily unavailable");
    }

    [Fact]
    public async Task PaystackVerificationService_HttpError_NoBody_ReturnsSafeFailureWithStatusCode()
    {
        var (svc, _) = Build(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(string.Empty),
        });

        var result = await svc.VerifyAsync("TX-REF-1");

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("500");
    }

    [Fact]
    public async Task PaystackVerificationService_TimeoutOrException_ReturnsSafeFailure()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("connection refused"));
        var (svc, _) = BuildWith(handler);

        var result = await svc.VerifyAsync("TX-REF-1");

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Contain("HttpRequestException",
            "wrapper must NEVER throw to callers — every failure surface returns a Result.Failure");
    }

    // ─── Auth header ───────────────────────────────────────────────

    [Fact]
    public async Task PaystackVerificationService_UsesAuthorizationBearerSecretKey()
    {
        var (svc, handler) = Build(OkJson(SuccessBody));

        await svc.VerifyAsync("TX-REF-1");

        var auth = handler.LastRequest!.Headers.Authorization;
        auth.Should().NotBeNull("every request must carry the Bearer token");
        auth!.Scheme.Should().Be("Bearer");
        auth.Parameter.Should().Be(ValidSecretKey);
    }

    // ─── Not-configured guard ─────────────────────────────────────

    [Fact]
    public async Task PaystackVerificationService_NotConfigured_ReturnsProviderNotConfigured()
    {
        var settings = new PaystackSettings
        {
            Enabled = false, // -> IsConfigured=false
            SecretKey = "", PublicKey = "", VerifyBaseUrl = "https://api.paystack.co/transaction/verify",
        };
        var (svc, handler) = Build(OkJson(SuccessBody), settings);

        var result = await svc.VerifyAsync("TX-REF-1");

        result.IsSuccess.Should().BeFalse();
        handler.LastRequest.Should().BeNull("no HTTP call should fire when Paystack is not configured");
    }

    [Fact]
    public async Task PaystackVerificationService_EmptyReference_ReturnsValidationError()
    {
        var (svc, handler) = Build(OkJson(SuccessBody));

        var result = await svc.VerifyAsync("");

        result.IsSuccess.Should().BeFalse();
        handler.LastRequest.Should().BeNull();
    }

    // ─── URL encoding for reference ────────────────────────────────

    [Fact]
    public async Task PaystackVerificationService_UrlEncodesReference()
    {
        var (svc, handler) = Build(OkJson(SuccessBody));

        await svc.VerifyAsync("TX/REF WITH SPACES");

        // Space encodes as %20; forward slash as %2F.
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Contain("TX%2FREF%20WITH%20SPACES");
    }

    // ─── Fake handler ─────────────────────────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public HttpRequestMessage? LastRequest { get; private set; }

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
            => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return _respond(request);
        }
    }
}
