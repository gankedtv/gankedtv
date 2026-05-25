namespace GankedTV.Api.Problems;

/// <summary>
/// RFC 7807 ProblemDetails factory that stamps a machine-readable <c>code</c> into
/// <c>extensions</c>. Use these in place of <c>Results.BadRequest(new { error = "..." })</c>
/// so every 4xx/5xx body has the same shape.
/// </summary>
public static class ProblemResults
{
    public const string CodeKey = "code";

    public static IResult BadRequest(string code, string? detail = null) =>
        Build(StatusCodes.Status400BadRequest, code, detail);

    public static IResult Unauthorized(string code, string? detail = null) =>
        Build(StatusCodes.Status401Unauthorized, code, detail);

    public static IResult Forbidden(string code, string? detail = null) =>
        Build(StatusCodes.Status403Forbidden, code, detail);

    public static IResult NotFound(string code, string? detail = null) =>
        Build(StatusCodes.Status404NotFound, code, detail);

    public static IResult Conflict(string code, string? detail = null) =>
        Build(StatusCodes.Status409Conflict, code, detail);

    public static IResult UnsupportedMediaType(string code, string? detail = null) =>
        Build(StatusCodes.Status415UnsupportedMediaType, code, detail);

    public static IResult TooManyRequests(string code, string? detail = null) =>
        Build(StatusCodes.Status429TooManyRequests, code, detail);

    public static IResult Internal(string code, string? detail = null) =>
        Build(StatusCodes.Status500InternalServerError, code, detail);

    public static IResult ServiceUnavailable(string code, string? detail = null) =>
        Build(StatusCodes.Status503ServiceUnavailable, code, detail);

    /// <summary>
    /// Shared null-body response used by both <c>ValidationEndpointFilter&lt;T&gt;</c> and
    /// handler-side defensive guards, so clients see one shape regardless of which guard fires.
    /// </summary>
    public static IResult InvalidBody() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["body"] = ["Request body is required."],
        });

    private static IResult Build(int status, string code, string? detail) =>
        Results.Problem(
            statusCode: status,
            detail: detail,
            extensions: new Dictionary<string, object?> { [CodeKey] = code });
}
