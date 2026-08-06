namespace InteractiveReport.Core.Model;

public enum ReportDialect
{
    SqlServer,
    Oracle,
    Sqlite,
    Postgres,
}

/// <summary>
/// Closed operator set. Serialized camelCase ("eq", "ncontains", ...).
/// Text-match operators (Contains/Ncontains/Starts/Ends) are case-insensitive by definition.
/// Blank/Nblank semantics are dialect-owned: on Oracle '' IS NULL, so "blank" is IS NULL;
/// elsewhere text-blank is (IS NULL OR = '').
/// </summary>
public enum FilterOp
{
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    Between,
    In,
    Nin,
    Contains,
    Ncontains,
    Starts,
    Ends,
    Blank,
    Nblank,
}

public enum AggregateFn
{
    Count,
    Sum,
    Avg,
    Min,
    Max,
    CountDistinct,
}

public enum SortDir
{
    Asc,
    Desc,
}
