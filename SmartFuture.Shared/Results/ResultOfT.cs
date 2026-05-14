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
}
