using System.Data.Common;
using InteractiveReport.Core.Model;
using Microsoft.Data.Sqlite;

namespace InteractiveReport.AspNetCore;

/// <summary>
/// Centralizes provider-token, connection-type, and dialect knowledge. SQLite is
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

    /// <summary>Maps ADO.NET invariant names from the ConnectionStrings <c>{name}_ProviderName</c> convention to provider tokens.</summary>
    private static readonly Dictionary<string, string> TokenByInvariantName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.Data.Sqlite"] = "sqlite",
        ["Microsoft.Data.SqlClient"] = "sqlServer",
        ["System.Data.SqlClient"] = "sqlServer",
        ["Npgsql"] = "postgres",
        ["Oracle.ManagedDataAccess.Client"] = "oracle",
    };

    /// <summary>Maps connection type names to dialects for code-registered factory inspection.</summary>
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

    /// <summary>Gets the supported provider tokens as a comma-separated diagnostic list.</summary>
    public static string TokenList => string.Join(", ", Providers.Select(p => p.Token));

    /// <summary>
    /// Attempts to resolve a configured provider token to its report dialect.
    /// </summary>
    /// <param name="token">The configured provider token to resolve.</param>
    /// <param name="dialect">Receives the token's fixed dialect when recognized.</param>
    /// <returns><see langword="true"/> when the token names a supported provider; otherwise, <see langword="false"/>.</returns>
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

    /// <summary>
    /// Returns a provider token in canonical casing.
    /// </summary>
    /// <param name="token">The configured provider token to canonicalize.</param>
    /// <returns>The canonical token, or <see langword="null"/> when unknown.</returns>
    public static string? CanonicalToken(string token)
        => ByToken.TryGetValue(token, out var provider) ? provider.Token : null;

    /// <summary>
    /// Resolves an ADO.NET invariant name to its provider token.
    /// </summary>
    /// <param name="invariantName">The ADO.NET provider invariant name.</param>
    /// <returns>The provider token, or <see langword="null"/> when the invariant name is unrecognized.</returns>
    public static string? TokenForInvariantName(string invariantName)
        => TokenByInvariantName.TryGetValue(invariantName.Trim(), out var token) ? token : null;

    /// <summary>Gets recognized ADO.NET invariant names as a comma-separated diagnostic list.</summary>
    public static string SupportedInvariantNames => string.Join(", ", TokenByInvariantName.Keys);

    /// <summary>
    /// Creates an unopened connection for a provider token. <paramref name="owner"/>
    /// names the config surface ("Report 'orders'", "SavedReports") in every error.
    /// </summary>
    /// <param name="token">The configured provider token.</param>
    /// <param name="connectionString">The connection string assigned to the new connection.</param>
    /// <param name="owner">The configuration owner named in validation errors.</param>
    /// <returns>A new unopened database connection owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the token is unknown, the provider assembly is unavailable, or the provider rejects the connection string.</exception>
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
    /// Returns the dialect a connection type implies, walking the inheritance chain so
    /// provider subclasses still match. Null when the type is not a recognized ADO.NET provider connection
    /// (wrappers, custom types).
    /// </summary>
    /// <param name="type">The concrete ADO.NET connection type to inspect.</param>
    /// <returns>The implied dialect, or <see langword="null"/> for an unrecognized wrapper or custom type.</returns>
    public static ReportDialect? FromConnectionType(Type type)
    {
        for (var candidate = type; candidate is not null && candidate != typeof(DbConnection); candidate = candidate.BaseType)
        {
            if (candidate.FullName is { } fullName && DialectByTypeFullName.TryGetValue(fullName, out var dialect))
                return dialect;
        }
        return null;
    }

    /// <summary>
    /// Looks up the connection-type table without loading provider assemblies; used by focused tests.
    /// </summary>
    /// <param name="fullName">The namespace-qualified connection type name to match.</param>
    /// <returns>The mapped dialect, or <see langword="null"/> when unrecognized.</returns>
    internal static ReportDialect? FromTypeFullName(string fullName)
        => DialectByTypeFullName.TryGetValue(fullName, out var dialect) ? dialect : null;
}
