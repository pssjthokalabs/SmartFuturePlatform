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

    // CORS response headers. These are written by the CORS middleware,
    // which sits INSIDE this one in the pipeline (see
    // ServiceExtensions.ConfigureMiddleware: ExceptionHandlingMiddleware
    // → … → UseCors → MapControllers).
    //
    // WHY THIS LIST EXISTS: `Response.Clear()` below wipes every header,
    // including `Access-Control-Allow-Origin`. The browser then sees a
    // 500 with no CORS headers and reports it as
    //   "No 'Access-Control-Allow-Origin' header is present" / net::ERR_FAILED
    // instead of surfacing our JSON error body. That turns EVERY
    // unhandled server exception into a misleading CORS failure and
    // hides the real cause from the frontend — which is exactly what
    // happened when a provider branch failed on live.
    //
    // So: snapshot these, clear, then put them back.
    private static readonly string[] CorsHeaderNames =
    {
        "Access-Control-Allow-Origin",
        "Access-Control-Allow-Credentials",
        "Access-Control-Allow-Headers",
        "Access-Control-Allow-Methods",
        "Access-Control-Expose-Headers",
        "Access-Control-Max-Age",
        "Vary"
    };

    private async Task WriteErrorResponse(HttpContext context, Exception ex, string traceId)
    {
        // If the response has already begun streaming we cannot rewrite
        // it — clearing would throw and mask the original exception.
        // Bail out; the already-sent bytes plus the logged exception are
        // all we have.
        if (context.Response.HasStarted)
        {
            _logger.LogWarning(
                "Response already started — cannot write JSON error body. TraceId={TraceId}", traceId);
            return;
        }

        // Preserve CORS headers across the Clear() so the browser can
        // actually READ this error instead of reporting a CORS failure.
        var preserved = new List<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>>();
        foreach (var name in CorsHeaderNames)
        {
            if (context.Response.Headers.TryGetValue(name, out var value))
                preserved.Add(new(name, value));
        }

        context.Response.Clear();

        foreach (var header in preserved)
            context.Response.Headers[header.Key] = header.Value;

        // Belt-and-braces: the CORS middleware may apply its headers via
        // an OnStarting callback that hasn't run yet (nothing has been
        // flushed), in which case there was nothing to preserve above.
        // Re-assert them here so an error is ALWAYS readable by the
        // browser. This mirrors the configured FrontendCors policy
        // exactly — SetIsOriginAllowed(_ => true) + AllowCredentials —
        // so it grants nothing the normal pipeline wouldn't.
        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrWhiteSpace(origin)
            && !context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"))
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = origin;
            context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            context.Response.Headers.Append("Vary", "Origin");
        }

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
