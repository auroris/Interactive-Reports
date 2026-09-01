using System.Data.Common;
using System.Net.Sockets;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using Xunit;

namespace InteractiveReport.Core.Tests;

public class DbErrorClassifierTests
{
    private sealed class FakeSqlException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed class FakeOracleException(int number, string message) : DbException(message)
    {
        public int Number { get; } = number;
    }

    private sealed class FakePostgresException(string sqlState, string message) : DbException(message)
    {
        public new string SqlState { get; } = sqlState;
    }

    private sealed class FakeSqliteException(int extendedErrorCode, string message) : DbException(message)
    {
        public int SqliteExtendedErrorCode { get; } = extendedErrorCode;
    }

    [Theory]
    [InlineData(18456, DbErrorKind.AuthenticationFailed, "Authentication", "18456")]
    [InlineData(18452, DbErrorKind.AuthenticationFailed, "Authentication", "18452")]
    [InlineData(4060, DbErrorKind.AuthenticationFailed, "Authentication", "4060")]
    [InlineData(229, DbErrorKind.PermissionDenied, "Permissions", "229")]
    [InlineData(230, DbErrorKind.PermissionDenied, "Permissions", "230")]
    [InlineData(262, DbErrorKind.PermissionDenied, "Permissions", "262")]
    [InlineData(208, DbErrorKind.ObjectNotFound, "Object Not Found", "208")]
    [InlineData(207, DbErrorKind.ObjectNotFound, "Object Not Found", "207")]
    [InlineData(53, DbErrorKind.ConnectionFailed, "Connection", "53")]
    [InlineData(3952, DbErrorKind.FeatureDisabled, "Configuration", "3952")]
    [InlineData(-2, DbErrorKind.Timeout, "Timeout", "-2")]
    [InlineData(2627, DbErrorKind.ConstraintViolation, "Constraint Violation", "2627")]
    public void Classify_sql_server_errors(int number, DbErrorKind expectedKind, string expectedCategory, string codeSubstring)
    {
        var ex = new FakeSqlException(number, $"SQL Server error {number}");
        var diagnosis = DbErrorClassifier.Classify(ReportDialect.SqlServer, ex);

        Assert.Equal(expectedKind, diagnosis.Kind);
        Assert.Equal(expectedCategory, diagnosis.Category);
        Assert.Contains(codeSubstring, diagnosis.ProviderCode);
        Assert.NotNull(diagnosis.RemediationHint);
        Assert.NotEmpty(diagnosis.RemediationHint);
    }

    [Theory]
    [InlineData(1017, DbErrorKind.AuthenticationFailed, "Authentication", "ORA-01017")]
    [InlineData(28000, DbErrorKind.AuthenticationFailed, "Authentication", "ORA-28000")]
    [InlineData(28001, DbErrorKind.AuthenticationFailed, "Authentication", "ORA-28001")]
    [InlineData(942, DbErrorKind.PermissionDenied, "Permissions", "ORA-00942")]
    [InlineData(1031, DbErrorKind.PermissionDenied, "Permissions", "ORA-01031")]
    [InlineData(1950, DbErrorKind.PermissionDenied, "Permissions", "ORA-01950")]
    [InlineData(12154, DbErrorKind.ConnectionFailed, "Connection", "ORA-12154")]
    [InlineData(12541, DbErrorKind.ConnectionFailed, "Connection", "ORA-12541")]
    [InlineData(904, DbErrorKind.SyntaxOrSchemaError, "Syntax", "ORA-00904")]
    [InlineData(1013, DbErrorKind.Timeout, "Timeout", "ORA-01013")]
    [InlineData(1, DbErrorKind.ConstraintViolation, "Constraint Violation", "ORA-00001")]
    public void Classify_oracle_errors(int number, DbErrorKind expectedKind, string expectedCategory, string expectedCode)
    {
        var ex = new FakeOracleException(number, $"Oracle error ORA-{number:D5}");
        var diagnosis = DbErrorClassifier.Classify(ReportDialect.Oracle, ex);

        Assert.Equal(expectedKind, diagnosis.Kind);
        Assert.Equal(expectedCategory, diagnosis.Category);
        Assert.Equal(expectedCode, diagnosis.ProviderCode);
        Assert.NotNull(diagnosis.RemediationHint);
        Assert.NotEmpty(diagnosis.RemediationHint);
    }

    [Theory]
    [InlineData("28P01", DbErrorKind.AuthenticationFailed, "Authentication")]
    [InlineData("28000", DbErrorKind.AuthenticationFailed, "Authentication")]
    [InlineData("42501", DbErrorKind.PermissionDenied, "Permissions")]
    [InlineData("08001", DbErrorKind.ConnectionFailed, "Connection")]
    [InlineData("08006", DbErrorKind.ConnectionFailed, "Connection")]
    [InlineData("42P01", DbErrorKind.ObjectNotFound, "Object Not Found")]
    [InlineData("42703", DbErrorKind.ObjectNotFound, "Object Not Found")]
    [InlineData("42601", DbErrorKind.SyntaxOrSchemaError, "Syntax")]
    [InlineData("57014", DbErrorKind.Timeout, "Timeout")]
    [InlineData("23505", DbErrorKind.ConstraintViolation, "Constraint Violation")]
    public void Classify_postgres_errors(string sqlState, DbErrorKind expectedKind, string expectedCategory)
    {
        var ex = new FakePostgresException(sqlState, $"PostgreSQL error SQLSTATE {sqlState}");
        var diagnosis = DbErrorClassifier.Classify(ReportDialect.Postgres, ex);

        Assert.Equal(expectedKind, diagnosis.Kind);
        Assert.Equal(expectedCategory, diagnosis.Category);
        Assert.Equal($"SQLSTATE {sqlState}", diagnosis.ProviderCode);
        Assert.NotNull(diagnosis.RemediationHint);
        Assert.NotEmpty(diagnosis.RemediationHint);
    }

    [Theory]
    [InlineData(14, DbErrorKind.ConnectionFailed, "Connection", "SQLITE_CANTOPEN")]
    [InlineData(8, DbErrorKind.PermissionDenied, "Permissions", "SQLITE_READONLY")]
    [InlineData(23, DbErrorKind.PermissionDenied, "Permissions", "SQLITE_AUTH")]
    [InlineData(5, DbErrorKind.ConcurrencyConflict, "Concurrency", "SQLITE_BUSY")]
    [InlineData(1555, DbErrorKind.ConstraintViolation, "Constraint Violation", "SQLITE_CONSTRAINT")]
    [InlineData(1, DbErrorKind.SyntaxOrSchemaError, "Syntax", "SQLITE_ERROR")]
    public void Classify_sqlite_errors(int extendedErrorCode, DbErrorKind expectedKind, string expectedCategory, string codePrefix)
    {
        var ex = new FakeSqliteException(extendedErrorCode, $"SQLite error {extendedErrorCode}");
        var diagnosis = DbErrorClassifier.Classify(ReportDialect.Sqlite, ex);

        Assert.Equal(expectedKind, diagnosis.Kind);
        Assert.Equal(expectedCategory, diagnosis.Category);
        Assert.StartsWith(codePrefix, diagnosis.ProviderCode);
        Assert.NotNull(diagnosis.RemediationHint);
        Assert.NotEmpty(diagnosis.RemediationHint);
    }

    [Fact]
    public void Classify_unwraps_aggregate_and_inner_exceptions()
    {
        var innerSqlEx = new FakeSqlException(18456, "Login failed for user 'sa'.");
        var wrapped = new AggregateException("One or more errors occurred.", new Exception("Wrapper", innerSqlEx));

        var diagnosis = DbErrorClassifier.Classify(ReportDialect.SqlServer, wrapped);

        Assert.Equal(DbErrorKind.AuthenticationFailed, diagnosis.Kind);
        Assert.Equal("Authentication", diagnosis.Category);
        Assert.Equal("SQL Server 18456", diagnosis.ProviderCode);
    }

    [Fact]
    public void Classify_socket_exception_as_connection_failed()
    {
        var socketEx = new SocketException((int)SocketError.ConnectionRefused);
        var diagnosis = DbErrorClassifier.Classify(ReportDialect.SqlServer, socketEx);

        Assert.Equal(DbErrorKind.ConnectionFailed, diagnosis.Kind);
        Assert.Equal("Connection", diagnosis.Category);
        Assert.Equal("SocketException", diagnosis.ProviderCode);
    }

    [Fact]
    public void Classify_generic_exception_with_message_fallback()
    {
        var authEx = new Exception("Database login failed for user 'demo'");
        var authDiagnosis = DbErrorClassifier.Classify(ReportDialect.SqlServer, authEx);
        Assert.Equal(DbErrorKind.AuthenticationFailed, authDiagnosis.Kind);

        var permEx = new Exception("Permission denied on object 'invoices'");
        var permDiagnosis = DbErrorClassifier.Classify(ReportDialect.SqlServer, permEx);
        Assert.Equal(DbErrorKind.PermissionDenied, permDiagnosis.Kind);

        var notFoundEx = new Exception("Table or view does not exist in schema");
        var notFoundDiagnosis = DbErrorClassifier.Classify(ReportDialect.SqlServer, notFoundEx);
        Assert.Equal(DbErrorKind.ObjectNotFound, notFoundDiagnosis.Kind);

        var connEx = new Exception("Could not connect to database server");
        var connDiagnosis = DbErrorClassifier.Classify(ReportDialect.SqlServer, connEx);
        Assert.Equal(DbErrorKind.ConnectionFailed, connDiagnosis.Kind);
    }

    [Fact]
    public void FormatDiagnostic_produces_readable_string()
    {
        var diagnosis = new DbErrorDiagnosis(
            DbErrorKind.PermissionDenied,
            "Permissions",
            "SQL Server 229",
            "Permission denied on database object.",
            "Grant SELECT permission on the table to the database user.");

        var formatted = diagnosis.FormatDiagnostic();

        Assert.Equal("[Permissions] (SQL Server 229) Permission denied on database object. Hint: Grant SELECT permission on the table to the database user.", formatted);
    }
}
