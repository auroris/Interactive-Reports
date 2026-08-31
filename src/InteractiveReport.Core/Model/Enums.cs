namespace InteractiveReport.Core.Model;

/// <summary>Identifies the SQL dialect used to compile and execute a report definition.</summary>
public enum ReportDialect
{
    /// <summary>Microsoft SQL Server.</summary>
    SqlServer,
    /// <summary>Oracle Database.</summary>
    Oracle,
    /// <summary>SQLite.</summary>
    Sqlite,
    /// <summary>PostgreSQL.</summary>
    Postgres,
}

/// <summary>
/// The consistency guarantee requested for one logical report execution. The
/// provider owns the mechanism: Snapshot uses SQL Server SNAPSHOT, an Oracle
/// read-only transaction, PostgreSQL REPEATABLE READ, or a SQLite read transaction.
/// None deliberately leaves each statement independent.
/// </summary>
public enum ReportConsistency
{
    /// <summary>Allows each statement to observe the database independently.</summary>
    None,
    /// <summary>Requires every statement in the execution to share one stable database snapshot.</summary>
    Snapshot,
}

/// <summary>Names an aggregate operation supported by report metrics and footers.</summary>
public enum AggregateFn
{
    /// <summary>Counts non-null values.</summary>
    Count,
    /// <summary>Adds numeric values.</summary>
    Sum,
    /// <summary>Computes the arithmetic mean of numeric values.</summary>
    Avg,
    /// <summary>Computes the median of numeric values.</summary>
    Median,
    /// <summary>Selects the minimum value.</summary>
    Min,
    /// <summary>Selects the maximum value.</summary>
    Max,
    /// <summary>Counts distinct non-null values.</summary>
    CountDistinct,
}

/// <summary>Specifies the direction of an ordered report column.</summary>
public enum SortDir
{
    /// <summary>Ascending order.</summary>
    Asc,
    /// <summary>Descending order.</summary>
    Desc,
}

/// <summary>Specifies whether null values sort before or after non-null values.</summary>
public enum NullPlacement
{
    /// <summary>Places null values first.</summary>
    First,
    /// <summary>Places null values last.</summary>
    Last,
}
