using InteractiveReport.Client.FileDownload;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using InteractiveReport.Core.Planning;
using InteractiveReport.Core.Validation;

namespace InteractiveReport.Core.Tests;

public static class TestFixtures
{
    public sealed record DownloadResult(
        IReadOnlyList<ColumnInfo> Columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
        bool Truncated);

    /// <summary>
    /// Exercises the production download boundary in engine integration tests: clone the
    /// document, request its complete bounded result, then let the file client shape CSV cells.
    /// </summary>
    public static async Task<DownloadResult> Download(
        this ReportExecutor executor,
        ReportDefinition definition,
        ReportState state,
        IReadOnlyDictionary<string, object?> contextParams,
        CancellationToken ct = default)
    {
        var document = ReportStateResolver.Resolve(defaults: null, state);
        document.Page ??= new PageRequest();
        document.Page.Index = 1;
        document.Page.Size = 0;
        var result = await executor.Query(definition, document, contextParams, ct);
        var table = CsvReportPresentation.Render(result);
        return new DownloadResult(
            table.Columns,
            table.Rows,
            result.TotalRows > result.Rows.Count);
    }

    /// <summary>
    /// Test-only shorthand for emitting ordinary composables. It deliberately is not
    /// part of the production report-document model.
    /// </summary>
    public sealed class StageLayer
    {
        public List<string>? Columns { get; set; }
        public Dictionary<string, string>? Labels { get; set; }
        public Dictionary<string, ColumnFormat>? Formats { get; set; }
        public List<ComputedColumn>? Computed { get; set; }
        public List<FilterRule>? Filters { get; set; }
        public List<SortRule>? Sorts { get; set; }
        public List<HighlightRule>? Highlights { get; set; }
        public List<string>? Breaks { get; set; }
        public List<AggregateRule>? Aggregates { get; set; }
    }

    public static readonly IReadOnlyList<ColumnModel> OrdersSchema =
    [
        Col("ORDER_ID", typeof(long)),
        Col("CUSTOMER", typeof(string)),
        Col("REGION", typeof(string)),
        Col("STATUS", typeof(string)),
        Col("AMOUNT", typeof(decimal)),
        Col("ORDER_DATE", typeof(DateTime)),
        Col("NOTES", typeof(string)),
    ];

    public static ReportDefinition OrdersDefinition(ReportDialect dialect) => new()
    {
        Name = "orders",
        Connection = "TestDb",
        Dialect = dialect,
        Sql = "SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES FROM ORDERS",
    };

    public static ColumnModel Col(string name, Type type) => new()
    {
        Name = name,
        Label = ColumnModel.Prettify(name),
        ClrType = type,
    };

    public static FilterRule Filter(string expression) => new() { Expr = expression };

    public static string PivotCellId(
        string tableId,
        string metricId,
        params object?[] key)
        => new DynamicPivotColumnIdentityRegistry([])
            .Register(tableId, metricId, key);

    // ---- composable table document construction shorthand ----

    /// <summary>A report document: SQL-backed table plus an optional derived table chain.</summary>
    public static ReportState Doc(
        StageLayer? source = null,
        IEnumerable<ReportTable>? tail = null,
        string? search = null,
        PageRequest? page = null,
        Dictionary<string, List<ReportTable>>? alternatives = null)
    {
        var tables = new Dictionary<string, ReportTable>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = new() { From = "definition", Composables = Composables(source) },
        };
        var active = "source";
        var index = 0;
        foreach (var table in tail ?? [])
        {
            table.From = active;
            var kind = table.Composables?.FirstOrDefault(composable =>
                composable.Kind is "group" or "pivot" or "chart")?.Kind ?? "table";
            active = $"{kind}{++index}";
            tables[active] = table;
        }
        foreach (var (name, configured) in alternatives ?? new())
        {
            var parent = "source";
            for (var configuredIndex = 0; configuredIndex < configured.Count; configuredIndex++)
            {
                var table = configured[configuredIndex];
                var id = configuredIndex == configured.Count - 1 ? name : $"{name}{configuredIndex + 1}";
                if (table is not null) table.From ??= parent;
                tables[id] = table!;
                parent = id;
            }
        }
        return new ReportState
        {
            Search = search,
            Page = page,
            ActiveTable = active,
            Tables = tables,
        };
    }

    public static ReportTable Group(
        string[] by,
        MetricRule[]? values = null,
        StageLayer? layer = null)
        => new()
        {
            Composables =
            [
                new TableComposable { Kind = "group", By = [.. by], Values = values?.ToList() },
                .. Composables(layer),
            ],
        };

    public static ReportTable Pivot(
        string[] rows,
        string[] cols,
        MetricRule[]? values = null,
        bool? totals = null,
        StageLayer? layer = null)
        => new()
        {
            Composables =
            [
                new TableComposable
                {
                    Kind = "pivot",
                    Rows = [.. rows],
                    Cols = [.. cols],
                    Values = values?.ToList(),
                    Totals = totals,
                },
                .. Composables(layer),
            ],
        };

    public static ReportTable ChartStage(Action<TableComposable> configure)
    {
        var shape = new TableComposable { Kind = "chart" };
        configure(shape);
        return new ReportTable { Composables = [shape] };
    }

    public static List<TableComposable> Composables(StageLayer? layer)
    {
        if (layer is null) return [];
        var result = new List<TableComposable>();
        if (layer.Computed is not null) result.Add(new TableComposable { Kind = "compute", Computed = layer.Computed });
        if (layer.Filters is not null) result.Add(new TableComposable { Kind = "filter", Filters = layer.Filters });
        if (layer.Sorts is not null) result.Add(new TableComposable { Kind = "sort", Sorts = layer.Sorts });
        if (layer.Breaks is not null) result.Add(new TableComposable { Kind = "break", Breaks = layer.Breaks });
        if (layer.Aggregates is not null) result.Add(new TableComposable { Kind = "aggregate", Aggregates = layer.Aggregates });
        if (layer.Highlights is not null) result.Add(new TableComposable { Kind = "highlight", Highlights = layer.Highlights });
        if (layer.Columns is not null) result.Add(new TableComposable { Kind = "select", Columns = layer.Columns });
        if (layer.Labels is not null) result.Add(new TableComposable { Kind = "labels", Labels = layer.Labels });
        if (layer.Formats is not null) result.Add(new TableComposable { Kind = "formats", Formats = layer.Formats });
        return result;
    }

    public static MetricRule Metric(string id, string col, AggregateFn fn)
        => new() { Id = id, Col = col, Fn = fn };
}
