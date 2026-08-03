using Microsoft.AspNetCore.Mvc;
using SmartFuture.Shared.Errors;
using SmartFuture.Shared.Results;

namespace SmartFuture.API.Controllers;

[ApiController]
public abstract class BaseController : ControllerBase
{
    protected IActionResult ToActionResult(Result result)
    {
        if (result.IsSuccess)
            return Ok(new { result.IsSuccess, result.Message });

        return MapFailure(result.Code, result.Message);
    }

    protected IActionResult ToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
            return Ok(new { result.IsSuccess, result.Message, result.Data });

        // Surface Data on failure too — endpoints like /api/auth/login
        // populate a diagnostic payload (e.g. EmailConfirmed /
        // PhoneNumberConfirmed flags) on ACCOUNT_VERIFICATION_REQUIRED
        // so the caller can route on observed state instead of guessing.
        return MapFailureWithData(result.Code, result.Message, result.Data);
    }

    private IActionResult MapFailure(string? code, string message)
    {
        var status = code switch
        {
            ErrorCodes.BAD_REQUEST or
            ErrorCodes.VALIDATION_ERROR or
            ErrorCodes.WEAK_PASSWORD or
            ErrorCodes.WEBHOOK_SIGNATURE_INVALID or
            ErrorCodes.VERIFICATION_CODE_INVALID or
            ErrorCodes.VERIFICATION_CODE_EXPIRED or
            ErrorCodes.VERIFICATION_CODE_ATTEMPTS_EXCEEDED => StatusCodes.Status400BadRequest,

            ErrorCodes.UNAUTHORIZED or
            ErrorCodes.INVALID_CREDENTIALS or
            ErrorCodes.INVALID_REFRESH_TOKEN or
            ErrorCodes.REFRESH_TOKEN_EXPIRED => StatusCodes.Status401Unauthorized,

            ErrorCodes.SMS_NOT_CONFIGURED => StatusCodes.Status503ServiceUnavailable,

            ErrorCodes.FORBIDDEN => StatusCodes.Status403Forbidden,

            ErrorCodes.NOT_FOUND => StatusCodes.Status404NotFound,

            ErrorCodes.CONFLICT or
            ErrorCodes.EMAIL_TAKEN or
            ErrorCodes.PHONE_TAKEN or
            ErrorCodes.PAYMENT_ALREADY_PAID or
            ErrorCodes.PAYMENT_AMOUNT_MISMATCH => StatusCodes.Status409Conflict,

            ErrorCodes.TOO_MANY_REQUESTS => StatusCodes.Status429TooManyRequests,

            ErrorCodes.PAYMENT_INIT_FAILED or
            ErrorCodes.UPSTREAM_UNAVAILABLE => StatusCodes.Status502BadGateway,

            ErrorCodes.PROVIDER_NOT_CONFIGURED => StatusCodes.Status503ServiceUnavailable,

            ErrorCodes.EXCEPTION => StatusCodes.Status500InternalServerError,

            _ => StatusCodes.Status500InternalServerError
        };

        return StatusCode(status, new
        {
            IsSuccess = false,
            Code = code,
            Message = message
        });
    }

    private IActionResult MapFailureWithData<T>(string? code, string message, T? data)
    {
        var statusCode = ResolveStatusCode(code);
        return StatusCode(statusCode, new
        {
            IsSuccess = false,
            Code = code,
            Message = message,
            Data = data
        });
    }

    private static int ResolveStatusCode(string? code) => code switch
    {
        ErrorCodes.BAD_REQUEST or
        ErrorCodes.VALIDATION_ERROR or
        ErrorCodes.WEAK_PASSWORD or
        ErrorCodes.WEBHOOK_SIGNATURE_INVALID or
        ErrorCodes.VERIFICATION_CODE_INVALID or
        ErrorCodes.VERIFICATION_CODE_EXPIRED or
        ErrorCodes.VERIFICATION_CODE_ATTEMPTS_EXCEEDED => StatusCodes.Status400BadRequest,

        ErrorCodes.UNAUTHORIZED or
        ErrorCodes.INVALID_CREDENTIALS or
        ErrorCodes.INVALID_REFRESH_TOKEN or
        ErrorCodes.REFRESH_TOKEN_EXPIRED or
        ErrorCodes.ACCOUNT_VERIFICATION_REQUIRED => StatusCodes.Status401Unauthorized,

        ErrorCodes.SMS_NOT_CONFIGURED => StatusCodes.Status503ServiceUnavailable,

        ErrorCodes.FORBIDDEN => StatusCodes.Status403Forbidden,

        ErrorCodes.NOT_FOUND => StatusCodes.Status404NotFound,

        ErrorCodes.CONFLICT or
        ErrorCodes.EMAIL_TAKEN or
        ErrorCodes.PHONE_TAKEN or
        ErrorCodes.ACCOUNT_EXISTS_SIGN_IN_REQUIRED or
        ErrorCodes.PAYMENT_ALREADY_PAID or
        ErrorCodes.PAYMENT_AMOUNT_MISMATCH => StatusCodes.Status409Conflict,

        ErrorCodes.TOO_MANY_REQUESTS => StatusCodes.Status429TooManyRequests,

        ErrorCodes.PAYMENT_INIT_FAILED or
        ErrorCodes.UPSTREAM_UNAVAILABLE => StatusCodes.Status502BadGateway,

        ErrorCodes.PROVIDER_NOT_CONFIGURED => StatusCodes.Status503ServiceUnavailable,

        ErrorCodes.EXCEPTION => StatusCodes.Status500InternalServerError,

        _ => StatusCodes.Status500InternalServerError
    };
}
