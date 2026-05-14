using System.Diagnostics;
using System.Net;
using System.Text.Json;
using SmartFuture.Shared.Errors;

namespace SmartFuture.API.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _env;
    private readonly IConfiguration _configuration;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment env,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _env = env;
        _configuration = configuration;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
            _logger.LogError(ex, "Unhandled exception. TraceId={TraceId}", traceId);
            await WriteErrorResponse(context, ex, traceId);
        }
    }

    private async Task WriteErrorResponse(HttpContext context, Exception ex, string traceId)
    {
        context.Response.Clear();
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        context.Response.ContentType = "application/json";

        var exposeDetails =
            _env.IsDevelopment() ||
            _configuration.GetValue("Diagnostics:ExposeExceptionDetails", false);

        var payload = new
        {
            IsSuccess = false,
            Code = ErrorCodes.EXCEPTION,
            Message = "An unexpected error occurred.",
            TraceId = traceId,
            Details = exposeDetails ? ex.ToString() : null
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await context.Response.WriteAsync(json);
    }
}
