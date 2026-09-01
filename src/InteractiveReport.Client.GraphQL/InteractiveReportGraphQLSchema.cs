// GraphQL schema entrypoint: exposes one dynamically named root field per authorized saved
// report. Field construction delegates execution to the shared GraphQL executor so schema
// discovery and runtime access follow the same policy.

using System.Globalization;
using GraphQL;
using GraphQL.Types;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.DependencyInjection;

namespace InteractiveReport.Client.GraphQL;

/// <summary>The schema exposed by <c>MapInteractiveReportGraphQL</c>.</summary>
public sealed class InteractiveReportGraphQLSchema : Schema
{
    /// <summary>
    /// Creates a schema whose query root exposes saved-report execution.
    /// </summary>
    /// <param name="services">The provider used by GraphQL.NET to resolve schema services.</param>
    /// <param name="query">The root query graph type.</param>
    public InteractiveReportGraphQLSchema(
        IServiceProvider services,
        InteractiveReportQueryGraphType query)
        : base(services)
    {
        Query = query;
    }
}

/// <summary>Defines the root <c>report</c> field and delegates its execution to the request-scoped executor.</summary>
public sealed class InteractiveReportQueryGraphType : ObjectGraphType
{
    /// <summary>
    /// Creates the query root with saved-report id and optional paging arguments.
    /// </summary>
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
                    context.GetArgument<long>("id"),
                    context.GetArgument<int?>("page"),
                    context.GetArgument<int?>("pageSize"),
                    context.CancellationToken);
            });
    }
}

/// <summary>Maps an engine <see cref="ReportResult"/> to the public GraphQL result object.</summary>
internal sealed class InteractiveReportResultGraphType : ObjectGraphType<ReportResult>
{
    /// <summary>
    /// Creates result fields for columns, rows, paging, row count, and elapsed time.
    /// </summary>
    public InteractiveReportResultGraphType()
    {
        Name = "InteractiveReportResult";

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<InteractiveReportColumnGraphType>>>>("columns")
            .Resolve(context => context.Source.Columns);
        Field<NonNullGraphType<ComplexScalarGraphType>>("rows")
            .Description(
                "Dynamic row objects keyed by the names in columns. Like the REST protocol, "
                + "64-bit integers and decimals are invariant strings so JavaScript clients "
                + "never lose digits to IEEE-754 doubles; column metadata still says 'number'.")
            .Resolve(context => WireRows(context.Source.Rows));
        Field<NonNullGraphType<InteractiveReportPageGraphType>>("page")
            .Resolve(context => context.Source.Page);
        Field<NonNullGraphType<LongGraphType>>("totalRows")
            .Resolve(context => context.Source.TotalRows);
        Field<NonNullGraphType<LongGraphType>>("elapsedMs")
            .Resolve(context => context.Source.ElapsedMs);
    }

    /// <summary>
    /// Converts dynamic rows to the GraphQL wire contract. This is the GraphQL twin of the REST
    /// exact-number converters: dynamic row
    /// values pass through GraphQL's own serializer, which would emit Int64/UInt64/Decimal as JSON numbers
    /// and silently round them in JavaScript clients. totalRows/elapsedMs stay Long scalars — their schema
    /// type declares number semantics and their magnitudes fit a double exactly.
    /// </summary>
    /// <param name="rows">The report rows to project, aggregate, or serialize.</param>
    /// <returns>New row dictionaries whose 64-bit integer and decimal values are invariant strings.</returns>
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> WireRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
        => rows
            .Select(row => (IReadOnlyDictionary<string, object?>)row.ToDictionary(
                pair => pair.Key,
                pair => WireValue(pair.Value),
                StringComparer.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Converts a result value into its GraphQL wire representation.
    /// </summary>
    /// <param name="value">The result value to convert into GraphQL-compatible scalars and collections.</param>
    /// <returns>An invariant string for <see cref="long"/>, <see cref="ulong"/>, and <see cref="decimal"/> values; otherwise, the original value.</returns>
    private static object? WireValue(object? value) => value switch
    {
        long number => number.ToString(CultureInfo.InvariantCulture),
        ulong number => number.ToString(CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        _ => value,
    };
}

/// <summary>Maps public column metadata to the GraphQL column object.</summary>
internal sealed class InteractiveReportColumnGraphType : ObjectGraphType<ColumnInfo>
{
    /// <summary>
    /// Creates fields for the column's identity, label, protocol type, computed flag, and optional pivot metric.
    /// </summary>
    public InteractiveReportColumnGraphType()
    {
        Name = "InteractiveReportColumn";

        Field<NonNullGraphType<StringGraphType>>("name").Resolve(context => context.Source.Name);
        Field<NonNullGraphType<StringGraphType>>("label").Resolve(context => context.Source.Label);
        Field<NonNullGraphType<StringGraphType>>("type").Resolve(context => context.Source.Type);
        Field<NonNullGraphType<BooleanGraphType>>("computed").Resolve(context => context.Source.Computed);
        Field<StringGraphType>("pivotMetricId").Resolve(context => context.Source.PivotMetricId);
    }
}

/// <summary>Maps the effective engine page request to the GraphQL page object.</summary>
internal sealed class InteractiveReportPageGraphType : ObjectGraphType<PageRequest>
{
    /// <summary>
    /// Creates fields for the one-based page index and page size.
    /// </summary>
    public InteractiveReportPageGraphType()
    {
        Name = "InteractiveReportPage";

        Field<NonNullGraphType<IntGraphType>>("index").Resolve(context => context.Source.Index);
        Field<NonNullGraphType<IntGraphType>>("size").Resolve(context => context.Source.Size);
    }
}
