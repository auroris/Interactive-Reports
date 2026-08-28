using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Model;
using Microsoft.Data.SqlClient;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace InteractiveReport.Live.Tests;

/// <summary>
/// This project is the only one referencing every ADO.NET provider, so it anchors
/// dialect sniffing against the real connection types (the unit suite covers the
/// same table through type-name strings). Not environment-gated: no database is
/// touched — types only.
/// </summary>
public sealed class ProviderTypeSniffTests
{
    [Fact]
    public void Real_provider_connection_types_sniff_to_their_dialects()
    {
        Assert.Equal(ReportDialect.SqlServer, ProviderCatalog.FromConnectionType(typeof(SqlConnection)));
        Assert.Equal(ReportDialect.Postgres, ProviderCatalog.FromConnectionType(typeof(NpgsqlConnection)));
        Assert.Equal(ReportDialect.Oracle, ProviderCatalog.FromConnectionType(typeof(OracleConnection)));
    }

    [Fact]
    public void Reflection_loading_finds_the_referenced_providers()
    {
        foreach (var token in new[] { "sqlServer", "postgres", "oracle" })
        {
            using var connection = ProviderCatalog.CreateConnection(token, "", $"token '{token}'");
            Assert.Equal(token, ProviderCatalog.CanonicalToken(token));
            Assert.NotNull(connection);
        }
    }
}
