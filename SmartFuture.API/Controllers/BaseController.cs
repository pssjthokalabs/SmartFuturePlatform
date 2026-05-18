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

        return MapFailure(result.Code, result.Message);
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
            ErrorCodes.PAYMENT_ALREADY_PAID or
            ErrorCodes.PAYMENT_AMOUNT_MISMATCH => StatusCodes.Status409Conflict,

            ErrorCodes.TOO_MANY_REQUESTS => StatusCodes.Status429TooManyRequests,

            ErrorCodes.PAYMENT_INIT_FAILED => StatusCodes.Status502BadGateway,

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
}
