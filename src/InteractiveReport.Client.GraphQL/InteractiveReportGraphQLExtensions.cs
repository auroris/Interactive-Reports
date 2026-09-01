// GraphQL adapter entrypoint: registration installs the schema, execution services, validation
// rule, and JSON transport; endpoint mapping then constrains the transport to one non-batched
// query over GET or POST. Report authorization remains inside each resolver.

using GraphQL;
using GraphQL.Server.Transports.AspNetCore;
using GraphQL.Types;
using InteractiveReport.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InteractiveReport.Client.GraphQL;

/// <summary>Registers and maps the optional GraphQL transport for Interactive Reports.</summary>
public static class InteractiveReportGraphQLExtensions
{
    /// <summary>
    /// Registers the GraphQL schema, graph types, executor, validation rule, and JSON serializer.
    /// </summary>
    /// <param name="services">The service collection in which to register Interactive Reports dependencies.</param>
    /// <returns>The service collection for further registrations.</returns>
    /// <remarks>Mutates <paramref name="services"/> by adding the adapter's dependencies.</remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code><![CDATA[
    /// builder.Services.AddInteractiveReports(builder.Configuration);
    /// builder.Services.AddInteractiveReportGraphQL();
    /// ]]></code>
    /// </example>
    public static IServiceCollection AddInteractiveReportGraphQL(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.TryAddScoped<InteractiveReportGraphQLExecutor>();
        services.TryAddSingleton<InteractiveReportQueryGraphType>();
        services.TryAddSingleton<InteractiveReportConfigurationGraphType>();
        services.TryAddSingleton<InteractiveReportSavedReportGraphType>();
        services.TryAddSingleton<InteractiveReportResultGraphType>();
        services.TryAddSingleton<InteractiveReportColumnGraphType>();
        services.TryAddSingleton<InteractiveReportPageGraphType>();
        services.TryAddSingleton<InteractiveReportIgnoredGraphType>();
        services.TryAddSingleton<InteractiveReportSortInputGraphType>();
        services.TryAddSingleton<InteractiveReportSortDirectionGraphType>();
        services.TryAddSingleton<InteractiveReportNullPlacementGraphType>();
        services.TryAddSingleton<ComplexScalarGraphType>();
        services.AddGraphQL(builder => builder
            .AddSchema<InteractiveReportGraphQLSchema>()
            .AddValidationRule<SingleRootFieldValidationRule>(useForCachedDocuments: true)
            .ConfigureExecutionOptions(options =>
            {
                if (options.Schema is InteractiveReportGraphQLSchema)
                {
                    options.MaxParallelExecutionCount = 1;
                }
            })
            .AddSystemTextJson());

        return services;
    }

    /// <summary>
    /// Maps the query-only GraphQL endpoint. Authentication remains the
    /// host's responsibility; each report resolver applies Interactive Reports authorization.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder on which to register the report routes.</param>
    /// <param name="path">The route pattern for the endpoint; defaults to <c>"/graphql"</c>.</param>
    /// <returns>The endpoint convention builder for further route customization.</returns>
    /// <remarks>Adds route metadata and filters to <paramref name="endpoints"/>. Each request disables caching and rejects unsupported methods, WebSockets, and batching.</remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpoints"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code><![CDATA[
    /// app.MapInteractiveReportGraphQL("/graphql")
    ///     .RequireRateLimiting("reports");
    /// ]]></code>
    /// </example>
    public static IEndpointConventionBuilder MapInteractiveReportGraphQL(
        this IEndpointRouteBuilder endpoints,
        string path = "/graphql")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("");
        group.AddEndpointFilter(InteractiveReportLogging.LogRequest);
        group.AddEndpointFilter(static async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            context.Response.Headers.CacheControl = "no-store";

            var request = context.Request;
            var isWebSocketUpgrade = request.Headers.TryGetValue("Upgrade", out var upgrade)
                && upgrade.Any(value => string.Equals(
                    value,
                    "websocket",
                    StringComparison.OrdinalIgnoreCase));
            if ((!HttpMethods.IsGet(request.Method) && !HttpMethods.IsPost(request.Method))
                || isWebSocketUpgrade)
            {
                context.Response.Headers.Allow = "GET, POST";
                const string code = InteractiveReportErrorCodes.GraphQlTransportUnsupported;
                var (title, description) = InteractiveReportErrorCatalog.Find(code);
                return Results.Json(
                    new InteractiveReportError(code, description, title),
                    IrJson.Options,
                    statusCode: StatusCodes.Status405MethodNotAllowed);
            }

            return await next(invocation);
        });
        return group.MapGraphQL<InteractiveReportGraphQLSchema>(path, options =>
        {
            options.HandleGet = true;
            options.HandlePost = true;
            options.HandleWebSockets = false;
            options.EnableBatchedRequests = false;
            options.ReadFormOnPost = false;
        });
    }
}
