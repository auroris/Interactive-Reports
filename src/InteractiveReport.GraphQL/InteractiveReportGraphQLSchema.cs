using GraphQL;
using GraphQL.Types;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.DependencyInjection;

namespace InteractiveReport.GraphQL;

/// <summary>The GraphQL schema exposed by <c>MapInteractiveReportGraphQL</c>.</summary>
public sealed class InteractiveReportGraphQLSchema : Schema
{
    public InteractiveReportGraphQLSchema(
        IServiceProvider services,
        InteractiveReportQueryGraphType query)
        : base(services)
    {
        Query = query;
    }
}

public sealed class InteractiveReportQueryGraphType : ObjectGraphType
{
    public InteractiveReportQueryGraphType()
    {
        Name = "Query";

        Field<InteractiveReportResultGraphType>("report")
            .Description("Executes a saved Interactive Report by id.")
            .Argument<NonNullGraphType<IdGraphType>>("id", "The saved-report id.")
            .Argument<IntGraphType>("page", "Optional 1-based page override.")
            .Argument<IntGraphType>("pageSize", "Optional page-size override; zero uses the engine's unpaged query mode.")
            .ResolveAsync(async context =>
            {
                var executor = context.RequestServices!.GetRequiredService<InteractiveReportGraphQLExecutor>();
                return await executor.Query(
                    context.GetArgument<string>("id"),
                    context.GetArgument<int?>("page"),
                    context.GetArgument<int?>("pageSize"),
                    context.CancellationToken);
            });
    }
}

internal sealed class InteractiveReportResultGraphType : ObjectGraphType<ReportResult>
{
    public InteractiveReportResultGraphType()
    {
        Name = "InteractiveReportResult";

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<InteractiveReportColumnGraphType>>>>("columns")
            .Resolve(context => context.Source.Columns);
        Field<NonNullGraphType<ComplexScalarGraphType>>("rows")
            .Description("Dynamic row objects keyed by the names in columns.")
            .Resolve(context => context.Source.Rows);
        Field<NonNullGraphType<InteractiveReportPageGraphType>>("page")
            .Resolve(context => context.Source.Page);
        Field<NonNullGraphType<LongGraphType>>("totalRows")
            .Resolve(context => context.Source.TotalRows);
        Field<NonNullGraphType<LongGraphType>>("elapsedMs")
            .Resolve(context => context.Source.ElapsedMs);
    }
}

internal sealed class InteractiveReportColumnGraphType : ObjectGraphType<ColumnInfo>
{
    public InteractiveReportColumnGraphType()
    {
        Name = "InteractiveReportColumn";

        Field<NonNullGraphType<StringGraphType>>("name").Resolve(context => context.Source.Name);
        Field<NonNullGraphType<StringGraphType>>("label").Resolve(context => context.Source.Label);
        Field<NonNullGraphType<StringGraphType>>("type").Resolve(context => context.Source.Type);
        Field<NonNullGraphType<BooleanGraphType>>("computed").Resolve(context => context.Source.Computed);
    }
}

internal sealed class InteractiveReportPageGraphType : ObjectGraphType<PageRequest>
{
    public InteractiveReportPageGraphType()
    {
        Name = "InteractiveReportPage";

        Field<NonNullGraphType<IntGraphType>>("index").Resolve(context => context.Source.Index);
        Field<NonNullGraphType<IntGraphType>>("size").Resolve(context => context.Source.Size);
    }
}
