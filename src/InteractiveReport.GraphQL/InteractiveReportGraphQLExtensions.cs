using GraphQL;
using GraphQL.Server.Transports.AspNetCore;
using GraphQL.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InteractiveReport.GraphQL;

/// <summary>Registration and endpoint mapping for the optional GraphQL adapter.</summary>
public static class InteractiveReportGraphQLExtensions
{
    /// <summary>Adds the Interactive Reports GraphQL schema and HTTP transport.</summary>
    public static IServiceCollection AddInteractiveReportGraphQL(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.TryAddScoped<InteractiveReportGraphQLExecutor>();
        services.TryAddSingleton<InteractiveReportQueryGraphType>();
        services.TryAddSingleton<InteractiveReportResultGraphType>();
        services.TryAddSingleton<InteractiveReportColumnGraphType>();
        services.TryAddSingleton<InteractiveReportPageGraphType>();
        services.TryAddSingleton<ComplexScalarGraphType>();
        services.AddGraphQL(builder => builder
            .AddSchema<InteractiveReportGraphQLSchema>()
            .AddSystemTextJson());

        return services;
    }

    /// <summary>
    /// Maps the query-only GraphQL endpoint. Authentication remains the host's
    /// responsibility; each report resolver applies Interactive Reports authorization.
    /// </summary>
    public static IEndpointConventionBuilder MapInteractiveReportGraphQL(
        this IEndpointRouteBuilder endpoints,
        string path = "/graphql")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup("");
        group.AddEndpointFilter(static async (invocation, next) =>
        {
            invocation.HttpContext.Response.Headers.CacheControl = "no-store";
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
