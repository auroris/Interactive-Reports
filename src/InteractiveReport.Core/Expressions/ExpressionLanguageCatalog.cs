namespace InteractiveReport.Core.Expressions;

/// <summary>Protocol metadata for clients that author report expressions.</summary>
public static class ExpressionLanguageCatalog
{
    public static IReadOnlyList<string> Functions => ExprFunctions.Names;
}
