using Microsoft.AspNetCore.Http;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Maps transport-neutral server failures onto the one HTTP shape every Interactive Reports client
/// returns. Adapters differ in what they produce on success — JSON, a file, a GraphQL envelope — but
/// a failure must look the same whichever route produced it, so the status mapping and the coded
/// error payload live here rather than being restated per client.
/// </summary>
public static class InteractiveReportHttpResult
{
    /// <summary>
    /// Selects the HTTP status for a classified server failure.
    /// </summary>
    /// <param name="kind">The transport-neutral failure classification.</param>
    /// <returns>The HTTP status code that represents it.</returns>
    public static int StatusFor(InteractiveReportFailureKind kind)
        => kind switch
        {
            InteractiveReportFailureKind.Invalid => StatusCodes.Status400BadRequest,
            InteractiveReportFailureKind.Unauthenticated => StatusCodes.Status401Unauthorized,
            InteractiveReportFailureKind.Forbidden => StatusCodes.Status403Forbidden,
            InteractiveReportFailureKind.NotFound => StatusCodes.Status404NotFound,
            InteractiveReportFailureKind.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };

    /// <summary>
    /// Builds an Interactive Reports HTTP error using the shared catalog and wire type.
    /// </summary>
    /// <param name="code">The stable protocol or diagnostic code to return.</param>
    /// <param name="statusCode">The HTTP status code to attach to the JSON result.</param>
    /// <param name="details">Optional request-specific details safe to expose to the caller.</param>
    /// <param name="traceId">Optional correlation id for server-side diagnostics.</param>
    /// <returns>A JSON result containing the stable code and catalog fallback text.</returns>
    public static IResult Error(
        string code,
        int statusCode,
        string? details = null,
        string? traceId = null)
    {
        var (title, description) = InteractiveReportErrorCatalog.Find(code);
        return Results.Json(
            new InteractiveReportError(code, description, title, details, traceId),
            IrJson.Options,
            statusCode: statusCode);
    }

    /// <summary>
    /// Translates a classified server failure into its HTTP response.
    /// </summary>
    /// <param name="failure">The classified server failure.</param>
    /// <param name="http">The active exchange, used to attach the challenge a 401 must carry.</param>
    /// <returns>The HTTP result carrying the failure's stable code.</returns>
    /// <remarks>
    /// A not-found result deliberately carries neither details nor a trace id: it is also the shape a
    /// hidden denial takes, and the two must stay indistinguishable to a caller.
    /// </remarks>
    public static IResult Failure(InteractiveReportFailure failure, HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(http);

        var status = StatusFor(failure.Kind);
        // RFC 9110: a 401 must name the challenge scheme. The engine authenticates nothing itself,
        // so the scheme only identifies which component demanded credentials.
        if (status == StatusCodes.Status401Unauthorized)
            http.Response.Headers.WWWAuthenticate = "InteractiveReport";
        return status == StatusCodes.Status404NotFound
            ? Error(failure.Code, status)
            : Error(failure.Code, status, failure.Details, failure.TraceIdentifier);
    }
}
