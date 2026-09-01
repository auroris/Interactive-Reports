using Microsoft.AspNetCore.Http;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Request-side plumbing every HTTP client shares: projecting the exchange onto the
/// transport-neutral request context and naming downloads. Adapters differ in what they
/// produce; how they read a request must not. (Body content types need no gate here: every
/// body-reading route declares <c>Accepts("application/json")</c>, which the framework enforces
/// with a 415 before any handler or filter runs.)
/// </summary>
public static class InteractiveReportHttpRequest
{
    /// <summary>
    /// Projects the current HTTP exchange onto the request context the server boundary consumes.
    /// </summary>
    /// <param name="http">The current HTTP request and response context.</param>
    /// <returns>The request context carrying the principal, request services, and trace id.</returns>
    public static InteractiveReportRequestContext Context(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new InteractiveReportRequestContext
        {
            User = http.User,
            RequestServices = http.RequestServices,
            TraceIdentifier = http.TraceIdentifier,
        };
    }

    /// <summary>
    /// Builds a filesystem-neutral download name: letters, digits, <c>.</c>, <c>-</c>, and <c>_</c>
    /// survive, everything else becomes <c>-</c>, and an empty stem falls back to <c>report</c>.
    /// </summary>
    /// <param name="stem">The human-readable name to sanitize.</param>
    /// <param name="extension">The extension to append, including its leading dot.</param>
    /// <returns>A name safe for a Content-Disposition header on every platform.</returns>
    public static string SafeFileName(string stem, string extension)
    {
        ArgumentNullException.ThrowIfNull(stem);
        ArgumentNullException.ThrowIfNull(extension);
        var safe = new string(stem.Select(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '-').ToArray()).Trim('.', '-');
        return (safe.Length == 0 ? "report" : safe) + extension;
    }
}
