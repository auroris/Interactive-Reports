using System.Data.Common;
using InteractiveReport.Core.Model;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// The one home for provider-token, connection-type, and dialect knowledge. SQLite is
/// a bundled provider; every other provider loads by reflection from the host's own
/// dependency graph, so the package
/// carries no provider references and a missing driver fails fast naming the exact
/// NuGet package to add. Dialect is derived, never chosen: a provider token fixes it
/// statically, and code-registered factories are sniffed by connection type.
/// </summary>
internal static class ProviderCatalog
{
    private sealed record Provider(
        string Token,
        ReportDialect Dialect,
        string DisplayName,
        string PackageHint,
        Lazy<Type?> ConnectionType);

    private static readonly Provider[] Providers =
    [
        new("sqlite", ReportDialect.Sqlite, "SQLite", "Microsoft.Data.Sqlite",
            new Lazy<Type?>(() => typeof(SqliteConnection))),
        new("sqlServer", ReportDialect.SqlServer, "SQL Server", "Microsoft.Data.SqlClient",
            new Lazy<Type?>(() =>
                Type.GetType("Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient")
                ?? Type.GetType("System.Data.SqlClient.SqlConnection, System.Data.SqlClient"))),
        new("postgres", ReportDialect.Postgres, "PostgreSQL", "Npgsql",
            new Lazy<Type?>(() => Type.GetType("Npgsql.NpgsqlConnection, Npgsql"))),
        new("oracle", ReportDialect.Oracle, "Oracle", "Oracle.ManagedDataAccess.Core",
            new Lazy<Type?>(() =>
                Type.GetType("Oracle.ManagedDataAccess.Client.OracleConnection, Oracle.ManagedDataAccess"))),
    ];

    private static readonly Dictionary<string, Provider> ByToken =
        Providers.ToDictionary(p => p.Token, StringComparer.OrdinalIgnoreCase);

    /// <summary>ADO.NET provider invariant names (the ConnectionStrings {name}_ProviderName convention).</summary>
    private static readonly Dictionary<string, string> TokenByInvariantName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Data.Sqlite"] = "sqlite",
        ["Microsoft.Data.SqlClient"] = "sqlServer",
        ["System.Data.SqlClient"] = "sqlServer",
        ["Npgsql"] = "postgres",
        ["Oracle.ManagedDataAccess.Client"] = "oracle",
    };

    /// <summary>Connection-type full name → dialect, for sniffing code-registered factories.</summary>
    private static readonly Dictionary<string, ReportDialect> DialectByTypeFullName = new(StringComparer.Ordinal)
    {
        ["Microsoft.Data.SqlClient.SqlConnection"] = ReportDialect.SqlServer,
        ["System.Data.SqlClient.SqlConnection"] = ReportDialect.SqlServer,
        ["Npgsql.NpgsqlConnection"] = ReportDialect.Postgres,
        ["Oracle.ManagedDataAccess.Client.OracleConnection"] = ReportDialect.Oracle,
        ["Oracle.DataAccess.Client.OracleConnection"] = ReportDialect.Oracle,
        ["Microsoft.Data.Sqlite.SqliteConnection"] = ReportDialect.Sqlite,
        ["System.Data.SQLite.SQLiteConnection"] = ReportDialect.Sqlite,
    };

    public static string TokenList => string.Join(", ", Providers.Select(p => p.Token));

    public static bool TryGetDialect(string token, out ReportDialect dialect)
    {
        if (ByToken.TryGetValue(token, out var provider))
        {
            dialect = provider.Dialect;
            return true;
        }
        dialect = default;
        return false;
    }

    /// <summary>The token's canonical casing, or null when unknown.</summary>
    public static string? CanonicalToken(string token)
        => ByToken.TryGetValue(token, out var provider) ? provider.Token : null;

    /// <summary>Provider token for an ADO.NET invariant name, or null when unrecognized.</summary>
    public static string? TokenForInvariantName(string invariantName)
        => TokenByInvariantName.TryGetValue(invariantName.Trim(), out var token) ? token : null;

    public static string SupportedInvariantNames => string.Join(", ", TokenByInvariantName.Keys);

    /// <summary>
    /// Creates an unopened connection for a provider token. <paramref name="owner"/>
    /// names the config surface ("Report 'orders'", "SavedReports") in every error.
    /// </summary>
    public static DbConnection CreateConnection(string token, string connectionString, string owner)
    {
        if (!ByToken.TryGetValue(token, out var provider))
            throw new InvalidOperationException(
                $"{owner}: unknown provider '{token}' (known: {TokenList}).");
        var type = provider.ConnectionType.Value
            ?? throw new InvalidOperationException(
                $"{owner} uses provider '{provider.Token}', but no {provider.DisplayName} ADO.NET provider assembly is loaded. "
                + $"Add a package reference to {provider.PackageHint}.");

        var connection = (DbConnection)Activator.CreateInstance(type)!;
        try
        {
            connection.ConnectionString = connectionString;
        }
        catch (Exception ex)
        {
            connection.Dispose();
            throw new InvalidOperationException(
                $"{owner}: {type.Name} rejected the connection string — {ex.Message}", ex);
        }
        return connection;
    }

    /// <summary>
    /// The dialect a connection instance implies, walking the inheritance chain so
    /// provider subclasses still match. Null when the type is not a recognized
    /// ADO.NET provider connection (wrappers, custom types).
    /// </summary>
    public static ReportDialect? FromConnectionType(Type type)
    {
        for (var candidate = type; candidate is not null && candidate != typeof(DbConnection); candidate = candidate.BaseType)
        {
            if (candidate.FullName is { } fullName && DialectByTypeFullName.TryGetValue(fullName, out var dialect))
                return dialect;
        }
        return null;
    }

    /// <summary>Test hook: the type-name table without needing the provider assemblies.</summary>
    internal static ReportDialect? FromTypeFullName(string fullName)
        => DialectByTypeFullName.TryGetValue(fullName, out var dialect) ? dialect : null;
}
