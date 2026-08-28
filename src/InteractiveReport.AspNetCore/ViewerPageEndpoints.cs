using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// The packaged browser pages: a viewer shell per report and one admin shell. Served
/// anonymously for the same reason the UI assets are — the shell is public package
/// markup containing zero report data, and it renders identically for any name, so
/// it discloses nothing (the element's schema request is the actual gate, and an
/// auth-gated page could not even tell a signed-out user to sign in). The script URL
/// is emitted as an absolute path under the mapped prefix, so the client's
/// script-relative api-base inference resolves without an api-base attribute.
/// </summary>
internal static class ViewerPageEndpoints
{
    internal static IResult Report(string name, HttpContext ctx)
    {
        if (!Enabled(ctx)) return Results.NotFound();

        var prefix = HtmlEncoder.Default.Encode(StripSegments(ctx, segments: 2));
        var encodedName = HtmlEncoder.Default.Encode(name);
        var savedReport = ctx.Request.Query["saved-report"].ToString();
        var savedAttribute = string.IsNullOrEmpty(savedReport)
            ? ""
            : $" saved-report=\"{HtmlEncoder.Default.Encode(savedReport)}\"";

        return Page(ctx, $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{encodedName}}</title>
            <style>
              body { margin: 0; font-family: system-ui, sans-serif; background: #f5f6f8; color: #1c2430; }
              main { max-width: 1280px; margin: 0 auto; padding: 16px; }
            </style>
            <script type="module" src="{{prefix}}/ui/ir.js"></script>
            </head>
            <body>
            <main>
            <interactive-report report="{{encodedName}}"{{savedAttribute}}>
              <p>This page needs JavaScript and the packaged report script ({{prefix}}/ui/ir.js).
              If this message persists, the script failed to load.</p>
            </interactive-report>
            </main>
            </body>
            </html>
            """);
    }

    internal static IResult Admin(HttpContext ctx)
    {
        if (!Enabled(ctx)) return Results.NotFound();

        var prefix = HtmlEncoder.Default.Encode(StripSegments(ctx, segments: 1));
        return Page(ctx, $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Saved report administration</title>
            <style>
              body { margin: 0; font-family: system-ui, sans-serif; background: #f5f6f8; color: #1c2430; }
              main { max-width: 1280px; margin: 0 auto; padding: 16px; }
            </style>
            <script type="module" src="{{prefix}}/ui/ir-admin.js"></script>
            </head>
            <body>
            <main>
            <interactive-report-admin>
              <p>This page needs JavaScript and the packaged administration script ({{prefix}}/ui/ir-admin.js).
              If this message persists, the script failed to load.</p>
            </interactive-report-admin>
            </main>
            </body>
            </html>
            """);
    }

    private static bool Enabled(HttpContext ctx)
        => ctx.RequestServices.GetRequiredService<IOptionsMonitor<InteractiveReportOptions>>()
            .CurrentValue.ViewerPagesEnabled;

    private static IResult Page(HttpContext ctx, string html)
    {
        // Trivially regenerated, and a stale shell across package upgrades would point
        // at mismatched bundles — nothing here is worth caching.
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// The mapped prefix, derived from the request's own escaped path with the route's
    /// trailing segments removed — exact under PathBase mounting and trailing-slash
    /// variants, where a relative script URL would not be.
    /// </summary>
    private static string StripSegments(HttpContext ctx, int segments)
    {
        var path = (ctx.Request.PathBase + ctx.Request.Path).ToString().TrimEnd('/');
        for (var i = 0; i < segments; i++)
        {
            var cut = path.LastIndexOf('/');
            if (cut < 0) return "";
            path = path[..cut];
        }
        return path;
    }
}
