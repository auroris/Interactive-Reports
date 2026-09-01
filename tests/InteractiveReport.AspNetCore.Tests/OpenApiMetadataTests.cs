using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace InteractiveReport.AspNetCore.Tests;

public sealed class OpenApiMetadataTests
{
    [Fact]
    public void Api_routes_publish_descriptions_tags_and_response_contracts()
    {
        using var app = WebApplication.CreateBuilder().Build();
        app.MapInteractiveReports("/api/reports");

        var routes = Routes(app);
        var described = routes.Where(route => !IsExcluded(route)).ToArray();

        Assert.NotEmpty(described);
        Assert.All(described, route =>
        {
            Assert.False(string.IsNullOrWhiteSpace(
                route.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary));
            Assert.False(string.IsNullOrWhiteSpace(
                route.Metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description));
            Assert.NotEmpty(route.Metadata.GetMetadata<ITagsMetadata>()?.Tags ?? []);
            Assert.Contains(
                route.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
                response => response.StatusCode is 200 or 201 or 204);
        });

        var query = Route(routes, "/api/reports/{name}/query", "POST");
        Assert.Equal(
            typeof(ReportState),
            query.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType);
        Assert.Contains(
            query.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            response => response.StatusCode == 200 && response.Type == typeof(ReportResult));

        var lov = Route(routes, "/api/reports/{name}/lov", "POST");
        Assert.Equal(
            typeof(ReportLovRequest),
            lov.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType);
        Assert.Contains(
            lov.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>(),
            response => response.StatusCode == 200 && response.Type == typeof(ReportLovResult));

        var create = Route(routes, "/api/reports/{name}/saved", "POST");
        Assert.Equal(
            typeof(SaveReportRequest),
            create.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType);

        var grant = Route(routes, "/api/reports/admin/authorization/administrators", "POST");
        Assert.Equal(
            typeof(AuthorizationIdentityRequest),
            grant.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType);
    }

    [Fact]
    public void Packaged_pages_and_assets_are_excluded_but_whoami_is_documented()
    {
        using var app = WebApplication.CreateBuilder().Build();
        app.MapInteractiveReports("/api/reports");

        var routes = Routes(app);
        Assert.True(IsExcluded(Route(routes, "/api/reports/ui/{file}", "GET")));
        Assert.True(IsExcluded(Route(routes, "/api/reports/{name}/view", "GET")));
        Assert.True(IsExcluded(Route(routes, "/api/reports/admin", "GET")));
        Assert.False(IsExcluded(Route(routes, "/api/reports/whoami", "GET")));
    }

    private static RouteEndpoint[] Routes(WebApplication app)
        => ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

    private static RouteEndpoint Route(
        IEnumerable<RouteEndpoint> routes,
        string pattern,
        string method)
        => Assert.Single(routes, route =>
            route.RoutePattern.RawText == pattern
            && route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

    private static bool IsExcluded(RouteEndpoint endpoint)
        => endpoint.Metadata.GetMetadata<IExcludeFromDescriptionMetadata>()
            ?.ExcludeFromDescription == true;
}
