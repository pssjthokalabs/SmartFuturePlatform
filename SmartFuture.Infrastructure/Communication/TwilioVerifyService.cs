using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartFuture.Application.Communication.Verification;
using SmartFuture.Infrastructure.Configuration;
using SmartFuture.Shared.Enums.Communication;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Verify.V2.Service;

namespace SmartFuture.Infrastructure.Communication;

public sealed class TwilioVerifyService : IPhoneVerificationService
{
    private readonly TwilioSettings _settings;
    private readonly ILogger<TwilioVerifyService> _logger;

    public TwilioVerifyService(IOptions<TwilioSettings> settings, ILogger<TwilioVerifyService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<Result<PhoneVerificationStartOutcome>> StartAsync(
        PhoneVerificationStartRequest request, CancellationToken cancellationToken = default)
    {
        var serviceSid = _settings.Verify.ServiceSid;
        if (string.IsNullOrWhiteSpace(serviceSid))
        {
            return Result<PhoneVerificationStartOutcome>.Failure(
                ErrorCodes.PROVIDER_NOT_CONFIGURED, "Twilio Verify service is not configured.");
        }

        var channel = request.Channel == MobileOtpChannel.WhatsApp ? "whatsapp" : "sms";

        _logger.LogInformation(
            "[TwilioVerify] Starting verification to={ToMasked} channel={Channel} purpose={Purpose}",
            MaskPhone(request.ToPhoneNumber), channel, request.Purpose);

        try
        {
            TwilioClient.Init(_settings.AccountSid, _settings.AuthToken);

            var verification = await VerificationResource.CreateAsync(
                to: request.ToPhoneNumber,
                channel: channel,
                pathServiceSid: serviceSid
            );

            _logger.LogInformation(
                "[TwilioVerify] Verification started sid={Sid} status={Status} channel={Channel}",
                verification.Sid, verification.Status, channel);

            return Result<PhoneVerificationStartOutcome>.Success(
                new PhoneVerificationStartOutcome("TwilioVerify", verification.Sid));
        }
        catch (ApiException ex) when (ex.Code == 60203)
        {
            _logger.LogWarning("[TwilioVerify] Rate limited for {ToMasked}: {Message}", MaskPhone(request.ToPhoneNumber), ex.Message);
            return Result<PhoneVerificationStartOutcome>.Failure(
                ErrorCodes.TOO_MANY_REQUESTS, "Please wait before requesting another code.");
        }
        catch (ApiException ex)
        {
            _logger.LogError(ex, "[TwilioVerify] API error starting verification: code={Code} message={Message}", ex.Code, ex.Message);
            return Result<PhoneVerificationStartOutcome>.Failure(
                ErrorCodes.UPSTREAM_UNAVAILABLE, "Could not send verification code. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TwilioVerify] Unexpected error starting verification");
            return Result<PhoneVerificationStartOutcome>.Failure(
                ErrorCodes.EXCEPTION, "Could not send verification code.");
        }
    }

    public async Task<Result<PhoneVerificationCheckOutcome>> CheckAsync(
        PhoneVerificationCheckRequest request, CancellationToken cancellationToken = default)
    {
        var serviceSid = _settings.Verify.ServiceSid;
        if (string.IsNullOrWhiteSpace(serviceSid))
        {
            return Result<PhoneVerificationCheckOutcome>.Failure(
                ErrorCodes.PROVIDER_NOT_CONFIGURED, "Twilio Verify service is not configured.");
        }

        _logger.LogInformation(
            "[TwilioVerify] Checking code for to={ToMasked} purpose={Purpose}",
            MaskPhone(request.ToPhoneNumber), request.Purpose);

        try
        {
            TwilioClient.Init(_settings.AccountSid, _settings.AuthToken);

            var check = await VerificationCheckResource.CreateAsync(
                to: request.ToPhoneNumber,
                code: request.Code,
                pathServiceSid: serviceSid
            );

            var approved = string.Equals(check.Status, "approved", StringComparison.OrdinalIgnoreCase);

            _logger.LogInformation(
                "[TwilioVerify] Check result status={Status} approved={Approved}",
                check.Status, approved);

            if (approved)
            {
                return Result<PhoneVerificationCheckOutcome>.Success(
                    new PhoneVerificationCheckOutcome(true, "TwilioVerify"));
            }

            return Result<PhoneVerificationCheckOutcome>.Failure(
                ErrorCodes.VERIFICATION_CODE_INVALID, "The code you entered is incorrect.");
        }
        catch (ApiException ex) when (ex.Code == 60202)
        {
            _logger.LogWarning("[TwilioVerify] Max attempts exceeded for {ToMasked}", MaskPhone(request.ToPhoneNumber));
            return Result<PhoneVerificationCheckOutcome>.Failure(
                ErrorCodes.VERIFICATION_CODE_ATTEMPTS_EXCEEDED, "Too many incorrect attempts. Please request a new code.");
        }
        catch (ApiException ex) when (ex.Code == 20404)
        {
            _logger.LogWarning("[TwilioVerify] Verification expired or not found for {ToMasked}", MaskPhone(request.ToPhoneNumber));
            return Result<PhoneVerificationCheckOutcome>.Failure(
                ErrorCodes.VERIFICATION_CODE_EXPIRED, "The code has expired. Please request a new one.");
        }
        catch (ApiException ex)
        {
            _logger.LogError(ex, "[TwilioVerify] API error checking verification: code={Code} message={Message}", ex.Code, ex.Message);
            return Result<PhoneVerificationCheckOutcome>.Failure(
                ErrorCodes.UPSTREAM_UNAVAILABLE, "Could not verify the code. Please try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TwilioVerify] Unexpected error checking verification");
            return Result<PhoneVerificationCheckOutcome>.Failure(
                ErrorCodes.EXCEPTION, "Could not verify the code.");
        }
    }

    private static string MaskPhone(string phone)
    {
        if (string.IsNullOrEmpty(phone) || phone.Length <= 4) return phone;
        return new string('*', phone.Length - 4) + phone[^4..];
    }
}
