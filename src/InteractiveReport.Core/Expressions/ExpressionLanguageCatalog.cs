namespace InteractiveReport.Core.Expressions;

/// <summary>Exposes protocol metadata needed by clients that author report expressions.</summary>
public static class ExpressionLanguageCatalog
{
    /// <summary>Gets the canonical, case-insensitive function names accepted by the expression parser.</summary>
    public static IReadOnlyList<string> Functions => ExprFunctions.Names;
}
