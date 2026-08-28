namespace InteractiveReport.Core.Model;

public enum ReportDialect
{
    SqlServer,
    Oracle,
    Sqlite,
    Postgres,
}

/// <summary>
/// The consistency guarantee requested for one logical report execution. The
/// provider owns the mechanism: Snapshot uses SQL Server SNAPSHOT, an Oracle
/// read-only transaction, Postgres REPEATABLE READ, or a SQLite read transaction.
/// None deliberately leaves each statement independent.
/// </summary>
public enum ReportConsistency
{
    None,
    Snapshot,
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
