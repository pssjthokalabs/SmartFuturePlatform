namespace SmartFuture.Shared.Results;

public class Result<T> : Result
{
    public T? Data { get; private init; }

    private Result() { }

    public static Result<T> Success(T data, string message = "OK") => new()
    {
        IsSuccess = true,
        Code = null,
        Message = message,
        Data = data
    };

    public new static Result<T> Failure(string code, string message) => new()
    {
        IsSuccess = false,
        Code = code,
        Message = message,
        Data = default
    };

    public new static Result<T> Failure(string message) => new()
    {
        IsSuccess = false,
        Code = null,
        Message = message,
        Data = default
    };

    // Failure with a diagnostic data payload. Used by endpoints like
    // /api/auth/login where the caller benefits from seeing the observed
    // state even when no token was minted — e.g. EmailConfirmed and
    // PhoneNumberConfirmed flags on an ACCOUNT_VERIFICATION_REQUIRED reject.
    public static Result<T> Failure(string code, string message, T? data) => new()
    {
        IsSuccess = false,
        Code = code,
        Message = message,
        Data = data
    };
}
