using GankedTV.Api.Problems;

namespace GankedTV.Api.Middleware;

/// <summary>
/// Catches unhandled exceptions from the pipeline and writes an RFC 7807
/// ProblemDetails response with <c>code = "internal_error"</c>. No stack trace
/// in the body — details go to the logger instead.
/// </summary>
public sealed class ErrorHandlingMiddleware : IMiddleware
{
    private readonly ILogger<ErrorHandlingMiddleware> _logger;

    public ErrorHandlingMiddleware(ILogger<ErrorHandlingMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                // Response is already on the wire; the best we can do is log and let the
                // client see a truncated body. Rethrowing here would just crash Kestrel.
                _logger.LogError(ex, "Unhandled exception after response started for {Path}", context.Request.Path);
                throw;
            }

            _logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);

            context.Response.Clear();
            var result = ProblemResults.Internal("internal_error");
            await result.ExecuteAsync(context);
        }
    }
}
