using System.Security.Cryptography;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace InteractiveReport.Client.Json;

/// <summary>
/// Serves the packaged UI bundles and source maps from embedded resources at
/// {prefix}/ui/{file}. The asset endpoint is anonymous by design: the files are public
/// code shipped in the package (anyone can read them on a feed — no data, no secrets),
/// and a session-expired page that cannot even load the script cannot tell the user to
/// sign in. Every data endpoint keeps the full authorization gate.
/// </summary>
internal static class UiEndpoints
{
    private const string ResourcePrefix = "InteractiveReport.Client.Json.Ui.";

    // ETags hash the content — an assembly-version tag would serve stale 304s across rebuilds
    // of the same version (bitten during development, would bite ops too).
    private static readonly Dictionary<string, (string ResourceName, string ETag)> Resources =
        typeof(UiEndpoints).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToDictionary(
                n => n[ResourcePrefix.Length..],
                n => (n, ComputeETag(n)),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Computes a stable content hash for one embedded asset.
    /// </summary>
    /// <param name="resourceName">The assembly manifest resource name.</param>
    /// <returns>A quoted ETag derived from the embedded resource content.</returns>
    /// <remarks>Reads and disposes the embedded resource stream.</remarks>
    private static string ComputeETag(string resourceName)
    {
        using var stream = typeof(UiEndpoints).Assembly.GetManifestResourceStream(resourceName)!;
        return $"\"{Convert.ToHexString(SHA256.HashData(stream))[..16]}\"";
    }

    /// <summary>
    /// Serves one known embedded asset with conditional-request and content-type handling.
    /// </summary>
    /// <param name="file">The route-relative embedded asset name.</param>
    /// <param name="ctx">The current HTTP request and response context.</param>
    /// <returns>The HTTP result to send to the client.</returns>
    /// <remarks>Writes cache validators to the response and opens an embedded stream when the asset is returned.</remarks>
    internal static IResult Serve(string file, HttpContext ctx)
    {
        // Invariant: pure dictionary lookup — the route value never touches the filesystem.
        if (!Resources.TryGetValue(file, out var entry))
            return Results.NotFound();

        ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        ctx.Response.Headers[HeaderNames.ETag] = entry.ETag;
        if (ctx.Request.Headers.IfNoneMatch.Any(v => v == entry.ETag))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var stream = typeof(UiEndpoints).Assembly.GetManifestResourceStream(entry.ResourceName)!;
        return Results.Stream(stream, ContentType(file));
    }

    /// <summary>
    /// Maps an embedded asset extension to its HTTP content type.
    /// </summary>
    /// <param name="file">The asset name whose extension determines the media type.</param>
    /// <returns>The HTTP content type for the requested asset.</returns>
    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".html" => "text/html; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".json" or ".map" => "application/json; charset=utf-8",
        _ => "application/octet-stream",
    };
}
