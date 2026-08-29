using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
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
        var language = Language(ctx);
        var fallback = language == "fr-CA"
            ? $"Cette page nécessite JavaScript et le script de rapport fourni ({prefix}/ui/ir.js). Si ce message demeure affiché, le chargement du script a échoué."
            : $"This page needs JavaScript and the packaged report script ({prefix}/ui/ir.js). If this message persists, the script failed to load.";
        var savedReport = ctx.Request.Query["saved-report"].ToString();
        var savedAttribute = string.IsNullOrEmpty(savedReport)
            ? ""
            : $" saved-report=\"{HtmlEncoder.Default.Encode(savedReport)}\"";

        return Page(ctx, $$"""
            <!doctype html>
            <html lang="{{language}}">
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
              <p>{{fallback}}</p>
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
        var language = Language(ctx);
        var title = language == "fr-CA"
            ? "Administration des rapports enregistrés"
            : "Saved report administration";
        var fallback = language == "fr-CA"
            ? $"Cette page nécessite JavaScript et le script d’administration fourni ({prefix}/ui/ir-admin.js). Si ce message demeure affiché, le chargement du script a échoué."
            : $"This page needs JavaScript and the packaged administration script ({prefix}/ui/ir-admin.js). If this message persists, the script failed to load.";
        return Page(ctx, $$"""
            <!doctype html>
            <html lang="{{language}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title>
            <style>
              body { margin: 0; font-family: system-ui, sans-serif; background: #f5f6f8; color: #1c2430; }
              main { max-width: 1280px; margin: 0 auto; padding: 16px; }
            </style>
            <script type="module" src="{{prefix}}/ui/ir-admin.js"></script>
            </head>
            <body>
            <main>
            <interactive-report-admin>
              <p>{{fallback}}</p>
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
    /// Respect an application's RequestLocalization middleware when present, then
    /// negotiate the two packaged locales directly for the standalone pages.
    /// Components embedded by a host continue to inherit that host page's lang.
    /// </summary>
    private static string Language(HttpContext ctx)
    {
        var requestCulture = ctx.Features.Get<IRequestCultureFeature>()
            ?.RequestCulture.UICulture.Name;
        var configured = SupportedLanguage(requestCulture);
        if (configured is not null) return configured;

        var bestLanguage = "en";
        var bestQuality = -1d;
        foreach (var item in ctx.Request.Headers.AcceptLanguage.ToString().Split(','))
        {
            var parts = item.Trim().Split(';', 2);
            var candidate = SupportedLanguage(parts[0]);
            if (candidate is null) continue;

            var quality = 1d;
            if (parts.Length == 2)
            {
                var parameter = parts[1].Trim();
                if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && (!double.TryParse(parameter[2..], System.Globalization.NumberStyles.AllowDecimalPoint,
                        System.Globalization.CultureInfo.InvariantCulture, out quality)
                        || quality <= 0))
                    continue;
            }
            if (quality <= bestQuality) continue;
            bestLanguage = candidate;
            bestQuality = quality;
        }
        return bestLanguage;
    }

    private static string? SupportedLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        if (language.Equals("fr", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("fr-", StringComparison.OrdinalIgnoreCase))
            return "fr-CA";
        if (language.Equals("en", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
            return "en";
        return null;
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
