// GraphQL schema entrypoint: exposes the caller's report catalogue, the saved documents of one
// configured report, and saved-report execution. Field construction delegates every operation to
// the shared GraphQL executor so schema discovery and runtime access follow the same policy.

using System.Globalization;
using GraphQL;
using GraphQL.Types;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using Microsoft.Extensions.DependencyInjection;

namespace InteractiveReport.Client.GraphQL;

/// <summary>The schema exposed by <c>MapInteractiveReportGraphQL</c>.</summary>
public sealed class InteractiveReportGraphQLSchema : Schema
{
    /// <summary>
    /// Creates a schema whose query root exposes report discovery and saved-report execution.
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

/// <summary>Defines the root discovery and execution fields and delegates them to the request-scoped executor.</summary>
public sealed class InteractiveReportQueryGraphType : ObjectGraphType
{
    /// <summary>
    /// Creates the query root with catalogue, saved-document, and saved-report execution fields.
    /// </summary>
    public InteractiveReportQueryGraphType()
    {
        Name = "Query";

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<InteractiveReportConfigurationGraphType>>>>("reports")
            .Description(
                "Lists the appsettings report configurations the caller may view. This is the "
                + "GraphQL twin of GET /api/reports; it lists configurations, not documents.")
            .ResolveAsync(async context => await Executor(context)
                .Configurations(context.CancellationToken));

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<InteractiveReportSavedReportGraphType>>>>("savedReports")
            .Description(
                "Lists the saved documents the caller may load for one configured report, the "
                + "GraphQL twin of GET /api/reports/{name}. Administrators receive the complete "
                + "family. Each id is accepted by the report field.")
            .Argument<NonNullGraphType<StringGraphType>>("report", "The appsettings report configuration name.")
            .ResolveAsync(async context => await Executor(context).SavedReports(
                context.GetArgument<string>("report"),
                context.CancellationToken));

        Field<InteractiveReportResultGraphType>("report")
            .Description("Executes a saved Interactive Report by id.")
            .Argument<NonNullGraphType<IdGraphType>>("id", "The saved-report id.")
            .Argument<IntGraphType>("page", "Optional 1-based page override.")
            .Argument<IntGraphType>("pageSize", "Optional page-size override; zero uses the engine's unpaged query mode.")
            .Argument<StringGraphType>(
                "search",
                "Optional replacement toolbar search text, matched case-insensitively across the "
                + "report's eligible text columns. An empty string clears the saved search; null "
                + "keeps it.")
            .Argument<ListGraphType<NonNullGraphType<InteractiveReportSortInputGraphType>>>(
                "sort",
                "Optional replacement ordering for the document's active table. An empty list "
                + "clears the saved ordering; null keeps it.")
            .ResolveAsync(async context => await Executor(context).Query(
                context.GetArgument<long>("id"),
                context.GetArgument<int?>("page"),
                context.GetArgument<int?>("pageSize"),
                context.GetArgument<string?>("search"),
                context.GetArgument<List<SortRule>?>("sort"),
                context.CancellationToken));
    }

    /// <summary>
    /// Resolves the request-scoped executor that owns authorization and execution.
    /// </summary>
    /// <param name="context">The resolver context for the executing field.</param>
    /// <returns>The request-scoped Interactive Reports GraphQL executor.</returns>
    private static InteractiveReportGraphQLExecutor Executor(IResolveFieldContext context)
        => context.RequestServices!.GetRequiredService<InteractiveReportGraphQLExecutor>();
}

/// <summary>Maps one visible appsettings report configuration to the GraphQL catalogue object.</summary>
internal sealed class InteractiveReportConfigurationGraphType : ObjectGraphType<ReportConfigurationSummary>
{
    /// <summary>
    /// Creates fields for the configuration's route name and display title.
    /// </summary>
    public InteractiveReportConfigurationGraphType()
    {
        Name = "InteractiveReportConfiguration";

        Field<NonNullGraphType<StringGraphType>>("name")
            .Description("The configuration name used by savedReports and the REST routes.")
            .Resolve(context => context.Source.Name);
        Field<NonNullGraphType<StringGraphType>>("title")
            .Description("The configuration's display title.")
            .Resolve(context => context.Source.Title);
    }
}

/// <summary>Maps saved-report metadata visible to the caller onto the GraphQL document object.</summary>
internal sealed class InteractiveReportSavedReportGraphType : ObjectGraphType<SavedReportSummary>
{
    /// <summary>
    /// Creates fields for the document's identity, family, title, sharing, ownership, and modification time.
    /// </summary>
    public InteractiveReportSavedReportGraphType()
    {
        Name = "InteractiveReportSavedReport";

        Field<NonNullGraphType<IdGraphType>>("id")
            .Description(
                "The document id accepted by the report field. Like every GraphQL ID it is a "
                + "string on the wire, so no digit is lost to a JavaScript double.")
            .Resolve(context => context.Source.Id);
        Field<NonNullGraphType<StringGraphType>>("reportName")
            .Description("The configuration this document belongs to.")
            .Resolve(context => context.Source.ReportName);
        Field<NonNullGraphType<StringGraphType>>("title")
            .Resolve(context => context.Source.Title);
        Field<NonNullGraphType<BooleanGraphType>>("isGlobal")
            .Description("Whether every authorized report user may load this document.")
            .Resolve(context => context.Source.IsGlobal);
        Field<NonNullGraphType<BooleanGraphType>>("isDefault")
            .Description("Whether this document is the family's default view.")
            .Resolve(context => context.Source.IsDefault);
        Field<NonNullGraphType<BooleanGraphType>>("mine")
            .Description("Whether the current caller owns this document.")
            .Resolve(context => context.Source.Mine);
        Field<NonNullGraphType<BooleanGraphType>>("isReadOnly")
            .Description("Whether the document is file-backed and therefore not editable through the API.")
            .Resolve(context => context.Source.IsReadOnly);
        Field<NonNullGraphType<DateTimeGraphType>>("modifiedUtc")
            .Resolve(context => context.Source.ModifiedUtc);
    }
}

/// <summary>Accepts one replacement sort rule for the executed document's active table.</summary>
internal sealed class InteractiveReportSortInputGraphType : InputObjectGraphType<SortRule>
{
    /// <summary>
    /// Creates the column, direction, and null-placement input fields.
    /// </summary>
    public InteractiveReportSortInputGraphType()
    {
        Name = "InteractiveReportSortInput";

        Field<NonNullGraphType<StringGraphType>>("col")
            .Description(
                "The logical column name, resolved against the report's live schema. Saved "
                + "reports degrade rather than fail, so an unknown or unsortable column is "
                + "dropped and reported in the result's ignored list.");
        Field<InteractiveReportSortDirectionGraphType>("dir")
            .Description("The sort direction; ASC when omitted.");
        Field<InteractiveReportNullPlacementGraphType>("nulls")
            .Description("Optional explicit null placement; omitting it keeps the database dialect's default.");
    }

    /// <summary>
    /// Builds the engine sort rule from the coerced input fields. The mapping is written out
    /// rather than reflected so an added protocol field cannot silently become GraphQL input.
    /// </summary>
    /// <param name="value">The coerced input-object fields.</param>
    /// <returns>The engine sort rule submitted with the report document.</returns>
    public override object ParseDictionary(IDictionary<string, object?> value)
    {
        var rule = new SortRule { Col = value.TryGetValue("col", out var col) ? col as string ?? "" : "" };
        if (value.TryGetValue("dir", out var dir) && dir is SortDir direction) rule.Dir = direction;
        if (value.TryGetValue("nulls", out var nulls) && nulls is NullPlacement placement) rule.Nulls = placement;
        return rule;
    }
}

/// <summary>Names the sort directions accepted by <c>InteractiveReportSortInput</c>.</summary>
internal sealed class InteractiveReportSortDirectionGraphType : EnumerationGraphType
{
    /// <summary>
    /// Registers the direction values under explicit GraphQL names. Naming them here keeps the
    /// schema independent of the host's global enum-naming switches.
    /// </summary>
    public InteractiveReportSortDirectionGraphType()
    {
        Name = "InteractiveReportSortDirection";
        Add("ASC", SortDir.Asc, "Ascending order.");
        Add("DESC", SortDir.Desc, "Descending order.");
    }
}

/// <summary>Names the null placements accepted by <c>InteractiveReportSortInput</c>.</summary>
internal sealed class InteractiveReportNullPlacementGraphType : EnumerationGraphType
{
    /// <summary>
    /// Registers the null-placement values under explicit GraphQL names.
    /// </summary>
    public InteractiveReportNullPlacementGraphType()
    {
        Name = "InteractiveReportNullPlacement";
        Add("FIRST", NullPlacement.First, "Places null values before non-null values.");
        Add("LAST", NullPlacement.Last, "Places null values after non-null values.");
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
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<InteractiveReportIgnoredGraphType>>>>("ignored")
            .Description(
                "State elements the engine dropped because they referenced columns that no "
                + "longer exist or features this relation cannot implement — including a sort "
                + "argument naming an unknown or unsortable column. Saved reports degrade "
                + "instead of failing, so this is where a silently discarded request surfaces.")
            .Resolve(context => context.Source.Ignored);
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

/// <summary>Maps one dropped report-state element to the GraphQL degradation object.</summary>
internal sealed class InteractiveReportIgnoredGraphType : ObjectGraphType<IgnoredItem>
{
    /// <summary>
    /// Creates fields for the dropped element's kind and diagnostic detail.
    /// </summary>
    public InteractiveReportIgnoredGraphType()
    {
        Name = "InteractiveReportIgnored";

        Field<NonNullGraphType<StringGraphType>>("kind")
            .Description("The report-state element that was dropped, such as 'sort'.")
            .Resolve(context => context.Source.Kind);
        Field<NonNullGraphType<StringGraphType>>("detail")
            .Description("Why it was dropped; English diagnostic text, not a localization key.")
            .Resolve(context => context.Source.Detail);
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
