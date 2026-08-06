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
}
