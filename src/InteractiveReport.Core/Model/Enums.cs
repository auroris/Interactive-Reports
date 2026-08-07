namespace InteractiveReport.Core.Model;

public enum ReportDialect
{
    SqlServer,
    Oracle,
    Sqlite,
    Postgres,
}

public enum AggregateFn
{
    Count,
    Sum,
    Avg,
    Median,
    Min,
    Max,
    CountDistinct,
}

public enum SortDir
{
    Asc,
    Desc,
}

public enum NullPlacement
{
    First,
    Last,
}
