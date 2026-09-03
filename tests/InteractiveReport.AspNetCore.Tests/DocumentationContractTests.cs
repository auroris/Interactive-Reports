using System.Text.RegularExpressions;
using GraphQL.Types;
using InteractiveReport.Client.GraphQL;
using InteractiveReport.Core.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace InteractiveReport.AspNetCore.Tests;

public sealed partial class DocumentationContractTests
{
    [Fact]
    public void Rest_reference_lists_every_mapped_route_and_method()
    {
        using var app = WebApplication.CreateBuilder().Build();
        app.MapInteractiveReportJson("/api/reports");
        app.MapInteractiveReportFileDownload("/api/download");

        var reference = ReadRepositoryFile("docs", "API.md");
        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>();

        foreach (var route in routes)
        {
            var pattern = RouteConstraint()
                .Replace(route.RoutePattern.RawText!, "{$1}")
                .TrimEnd('/');
            var methods = route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
            Assert.All(methods, method => Assert.Contains($"`{method} {pattern}`", reference));
        }
    }

    [Fact]
    public void Authorization_reference_lists_every_public_action()
    {
        var reference = ReadRepositoryFile("docs", "AUTHORIZATION.md");

        Assert.All(
            Enum.GetNames<InteractiveReportAction>(),
            action => Assert.Contains($"`{action}`", reference));
    }

    [Fact]
    public void Api_reference_lists_every_feature_token()
    {
        var reference = ReadRepositoryFile("docs", "API.md");

        Assert.All(
            ReportFeatures.All,
            feature => Assert.Contains($"`{feature}`", reference));
    }

    [Fact]
    public void Graphql_reference_lists_report_arguments_with_nullability()
    {
        var reference = ReadRepositoryFile("docs", "GRAPHQL.md");
        var query = new InteractiveReportQueryGraphType();
        var report = Assert.Single(query.Fields, field => field.Name == "report");

        Assert.NotNull(report.Arguments);
        Assert.All(report.Arguments, argument =>
        {
            Assert.NotNull(argument.Type);
            Assert.Contains(
                $"{argument.Name}: {GraphQlTypeName(argument.Type)}",
                reference);
        });
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "InteractiveReport.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine([directory.FullName, .. segments]));
    }

    private static string GraphQlTypeName(Type type)
    {
        if (type.IsGenericType)
        {
            var inner = GraphQlTypeName(type.GetGenericArguments()[0]);
            if (type.GetGenericTypeDefinition() == typeof(NonNullGraphType<>)) return $"{inner}!";
            if (type.GetGenericTypeDefinition() == typeof(ListGraphType<>)) return $"[{inner}]";
        }

        var name = type.Name.EndsWith("GraphType", StringComparison.Ordinal)
            ? type.Name[..^"GraphType".Length]
            : type.Name;
        return name == "Id" ? "ID" : name;
    }

    [GeneratedRegex(@"\{([^}:]+):[^}]+\}")]
    private static partial Regex RouteConstraint();
}
