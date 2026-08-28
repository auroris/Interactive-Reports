using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Tests;

public static class TestFixtures
{
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

    // ---- v3 pipeline document construction shorthand ----

    /// <summary>A v3 document: source stage with the given layer plus optional tail stages.</summary>
    public static ReportState Doc(
        StageLayer? source = null,
        IEnumerable<PipelineStage>? tail = null,
        string? search = null,
        PageRequest? page = null,
        Dictionary<string, List<PipelineStage>>? shelf = null)
    {
        var pipeline = new List<PipelineStage>
        {
            new() { Shape = new StageShape { Kind = "source" }, Layer = source },
        };
        if (tail is not null) pipeline.AddRange(tail);
        return new ReportState
        {
            Search = search,
            Page = page,
            Pipeline = pipeline,
            Shelf = shelf,
        };
    }

    public static PipelineStage Group(
        string[] by,
        MetricRule[]? values = null,
        StageLayer? layer = null)
        => new()
        {
            Shape = new StageShape { Kind = "group", By = [.. by], Values = values?.ToList() },
            Layer = layer,
        };

    public static PipelineStage Spread(string[] cols, bool? totals = null, StageLayer? layer = null)
        => new()
        {
            Shape = new StageShape { Kind = "spread", Cols = [.. cols], Totals = totals },
            Layer = layer,
        };

    public static PipelineStage ChartStage(Action<StageShape> configure)
    {
        var shape = new StageShape { Kind = "chart" };
        configure(shape);
        return new PipelineStage { Shape = shape };
    }

    public static MetricRule Metric(string id, string col, AggregateFn fn)
        => new() { Id = id, Col = col, Fn = fn };
}
