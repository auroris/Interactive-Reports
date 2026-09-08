using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Xunit.Sdk;

namespace InteractiveReport.Core.Tests;

/// <summary>
/// Fails the running test when engine SQL leaves Oracle 11g's vocabulary. Oracle 11g mode runs
/// against a modern server, which would silently accept 12c syntax; this guard says what an
/// 11g server would have said. It inspects every statement the engine logs immediately before
/// execution, so a violation surfaces in the test that produced it.
/// </summary>
internal sealed class Oracle11gVocabularyGuard<T> : ILogger<T>
{
    private const string SqlPrefix = "Executing report SQL:";
    private const int MaxIdentifierBytes = 30;

    private static readonly (Regex Pattern, string Construct)[] PostElevenG =
    [
        (new(@"\bOFFSET\s+\S+\s+ROWS?\b", RegexOptions.IgnoreCase), "OFFSET (12c row limiting)"),
        (new(@"\bFETCH\s+(FIRST|NEXT)\b", RegexOptions.IgnoreCase), "FETCH FIRST/NEXT (12c row limiting)"),
        (new(@"\bGENERATED\s+(ALWAYS|BY\s+DEFAULT)\b", RegexOptions.IgnoreCase), "identity column (12c)"),
        (new(@"\b(CROSS|OUTER)\s+APPLY\b", RegexOptions.IgnoreCase), "APPLY (12c)"),
        (new(@"\bLATERAL\b", RegexOptions.IgnoreCase), "LATERAL (12c)"),
        (new(@"\bJSON_\w+\s*\(", RegexOptions.IgnoreCase), "JSON functions (12c)"),
        (new(@"\bON\s+OVERFLOW\b", RegexOptions.IgnoreCase), "LISTAGG ON OVERFLOW (12.2)"),
        (new(@"\bON\s+CONVERSION\s+ERROR\b", RegexOptions.IgnoreCase), "DEFAULT ON CONVERSION ERROR (12.2)"),
        (new(@"\bVALIDATE_CONVERSION\s*\(", RegexOptions.IgnoreCase), "VALIDATE_CONVERSION (12.2)"),
        (new(@"\bAPPROX_\w+\s*\(", RegexOptions.IgnoreCase), "approximate aggregates (12c)"),
        (new(@"\bMATCH_RECOGNIZE\b", RegexOptions.IgnoreCase), "MATCH_RECOGNIZE (12c)"),
        (new(@"\bCOLLATE\b", RegexOptions.IgnoreCase), "COLLATE (12.2)"),
        (new(@"\bWITH\s+FUNCTION\b", RegexOptions.IgnoreCase), "WITH FUNCTION (12c)"),
    ];

    // 11g caps every identifier and bind name at 30 bytes (12.2 raised it to 128).
    private static readonly Regex QuotedIdentifier = new("\"((?:[^\"]|\"\")+)\"");
    private static readonly Regex BindName = new(@":([A-Za-z][A-Za-z0-9_$#]*)");

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (!message.StartsWith(SqlPrefix, StringComparison.Ordinal)) return;
        var sql = message[SqlPrefix.Length..].TrimStart();

        foreach (var (pattern, construct) in PostElevenG)
        {
            if (pattern.IsMatch(sql))
                throw new XunitException($"Oracle 11g vocabulary violation: {construct} in\n{sql}");
        }
        foreach (Match match in QuotedIdentifier.Matches(sql))
            RequireLength(match.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal), sql);
        foreach (Match match in BindName.Matches(sql))
            RequireLength(match.Groups[1].Value, sql);
    }

    private static void RequireLength(string identifier, string sql)
    {
        if (Encoding.UTF8.GetByteCount(identifier) > MaxIdentifierBytes)
            throw new XunitException(
                $"Oracle 11g identifier limit: '{identifier}' exceeds {MaxIdentifierBytes} bytes in\n{sql}");
    }
}
