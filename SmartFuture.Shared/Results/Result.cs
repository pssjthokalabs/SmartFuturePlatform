namespace SmartFuture.Shared.Results;

public class Result
{
    public bool IsSuccess { get; protected init; }
    public string? Code { get; protected init; }
    public string Message { get; protected init; } = string.Empty;

    protected Result() { }

    public static Result Success(string message = "OK") => new()
    {
        IsSuccess = true,
        Code = null,
        Message = message
    };

    public static Result Failure(string code, string message) => new()
    {
        IsSuccess = false,
        Code = code,
        Message = message
    };

    public static Result Failure(string message) => new()
    {
        IsSuccess = false,
        Code = null,
        Message = message
    };
}
