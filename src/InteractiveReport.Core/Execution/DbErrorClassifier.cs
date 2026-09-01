using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Sockets;
using System.Reflection;
using InteractiveReport.Core.Model;

namespace InteractiveReport.Core.Execution;

/// <summary>
/// Categorizes database errors to make developer diagnosis and remediation straightforward.
/// </summary>
public enum DbErrorKind
{
    /// <summary>Authentication or logon failed (e.g. wrong username/password, expired password, account locked).</summary>
    AuthenticationFailed,

    /// <summary>Insufficient privileges or access denied (e.g. permission denied on table/column/schema, cannot create table).</summary>
    PermissionDenied,

    /// <summary>Could not connect to the database server (e.g. network unreachable, host not found, port closed, listener down).</summary>
    ConnectionFailed,

    /// <summary>Database object (table, view, or column) does not exist.</summary>
    ObjectNotFound,

    /// <summary>SQL syntax error or unsupported SQL statement construct.</summary>
    SyntaxOrSchemaError,

    /// <summary>Execution timeout expired or statement was cancelled due to timeout.</summary>
    Timeout,

    /// <summary>Database concurrency conflict (e.g. deadlock, lock timeout, database busy/locked).</summary>
    ConcurrencyConflict,

    /// <summary>Integrity constraint violation (e.g. unique constraint, primary key, foreign key, check constraint).</summary>
    ConstraintViolation,

    /// <summary>Database feature or configuration prerequisite is disabled (e.g. SQL Server snapshot isolation disabled).</summary>
    FeatureDisabled,

    /// <summary>Unclassified database error.</summary>
    Unclassified,
}

/// <summary>
/// Contains structured diagnosis and actionable remediation guidance for a database error.
/// </summary>
/// <param name="Kind">The broad category of the database error.</param>
/// <param name="Category">Human-readable name of the category (e.g. "Authentication", "Permissions", "Connection", "Object Not Found").</param>
/// <param name="ProviderCode">The provider-specific error number or SQLSTATE code, when available.</param>
/// <param name="Summary">A concise description of the failure.</param>
/// <param name="RemediationHint">Actionable advice for the developer or administrator on how to resolve the issue.</param>
public sealed record DbErrorDiagnosis(
    DbErrorKind Kind,
    string Category,
    string? ProviderCode,
    string Summary,
    string? RemediationHint)
{
    /// <summary>
    /// Formats a human-readable diagnostic description including category, code, summary, and hint.
    /// </summary>
    /// <returns>A formatted diagnostic string.</returns>
    public string FormatDiagnostic()
    {
        var codePart = ProviderCode is not null ? $" ({ProviderCode})" : "";
        var hintPart = RemediationHint is not null ? $" Hint: {RemediationHint}" : "";
        return $"[{Category}]{codePart} {Summary}{hintPart}";
    }
}

/// <summary>
/// Classifies provider exceptions per dialect without referencing provider
/// assemblies: provider-specific codes are read through cached reflection (the same
/// technique CommandBuilder uses for Oracle's BindByName), with the standard
/// DbException.SqlState where the provider populates it (Npgsql).
/// </summary>
public static class DbErrorClassifier
{
    private static readonly ConcurrentDictionary<(Type Type, string Property), Func<DbException, int?>?> IntGetters = new();
    private static readonly ConcurrentDictionary<(Type Type, string Property), Func<DbException, string?>?> StringGetters = new();

    /// <summary>
    /// Determines whether the exception is that dialect's unique-constraint or unique-index
    /// violation — the only insert failure an upsert may treat as "the row exists". SQL Server: 2627
    /// (constraint) / 2601 (index); Oracle: ORA-00001; PostgreSQL: SQLSTATE 23505; SQLite: extended result
    /// codes 1555 (primary key) / 2067 (unique).
    /// </summary>
    /// <param name="dialect">The database dialect whose SQL rules apply.</param>
    /// <param name="exception">The exception whose provider-specific details are being classified or logged.</param>
    /// <returns><see langword="true"/> when the exception reports a uniqueness violation; otherwise, <see langword="false"/>.</returns>
    public static bool IsUniqueViolation(ReportDialect dialect, DbException exception) => dialect switch
    {
        ReportDialect.SqlServer => IntProperty(exception, "Number") is 2627 or 2601,
        ReportDialect.Oracle => IntProperty(exception, "Number") == 1,
        ReportDialect.Postgres => string.Equals(GetSqlState(exception), "23505", StringComparison.Ordinal),
        ReportDialect.Sqlite => IntProperty(exception, "SqliteExtendedErrorCode") is 1555 or 2067,
        _ => false,
    };

    /// <summary>
    /// Classifies an exception into a structured diagnosis with provider error code, summary, and actionable remediation hint.
    /// </summary>
    /// <param name="dialect">The database dialect associated with the operation.</param>
    /// <param name="exception">The exception that occurred during a database operation.</param>
    /// <returns>A structured diagnosis containing error kind, category, code, summary, and remediation guidance.</returns>
    public static DbErrorDiagnosis Classify(ReportDialect dialect, Exception exception)
    {
        var dbEx = UnwrapDbException(exception);
        if (dbEx is not null)
        {
            var classified = dialect switch
            {
                ReportDialect.SqlServer => ClassifySqlServer(dbEx),
                ReportDialect.Oracle => ClassifyOracle(dbEx),
                ReportDialect.Postgres => ClassifyPostgres(dbEx),
                ReportDialect.Sqlite => ClassifySqlite(dbEx),
                _ => null,
            };

            if (classified is not null) return classified;
        }

        return ClassifyGeneric(exception);
    }

    /// <summary>
    /// Classifies SQL Server provider exceptions by SQL Server error number.
    /// </summary>
    private static DbErrorDiagnosis? ClassifySqlServer(DbException ex)
    {
        var number = IntProperty(ex, "Number");
        if (!number.HasValue) return null;

        var code = number.Value.ToString();
        return number.Value switch
        {
            // Authentication failures
            18456 => new DbErrorDiagnosis(
                DbErrorKind.AuthenticationFailed,
                "Authentication",
                $"SQL Server {code}",
                "Login failed for user. Incorrect username or password, or the login is disabled.",
                "Check the username and password in the connection string or connection factory, and verify the login is enabled on SQL Server."),
            18452 => new DbErrorDiagnosis(
                DbErrorKind.AuthenticationFailed,
                "Authentication",
                $"SQL Server {code}",
                "Login failed. The login is from an untrusted domain and cannot be used with Integrated Security.",
                "Verify domain trust settings, or use SQL Server authentication with User Id and Password."),
            4060 => new DbErrorDiagnosis(
                DbErrorKind.AuthenticationFailed,
                "Authentication",
                $"SQL Server {code}",
                "Cannot open database requested by the login. The database may not exist or the user lacks access.",
                "Check that the initial catalog / database name exists and that the database user has access permissions to it."),
            18450 => new DbErrorDiagnosis(
                DbErrorKind.AuthenticationFailed,
                "Authentication",
                $"SQL Server {code}",
                "Login failed. User is not defined as a valid user of a trusted SQL Server connection.",
                "Configure the login on SQL Server or update credentials in the connection configuration."),

            // Permissions / Access denied
            229 => new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                $"SQL Server {code}",
                "Permission denied on database object (table, view, or stored procedure).",
                "Grant SELECT, INSERT, UPDATE, or EXECUTE permission on the database object to the database user or role: 'GRANT SELECT ON <object> TO <user>;'."),
            230 => new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                $"SQL Server {code}",
                "Permission denied on column of database object.",
                "Grant column-level SELECT permissions to the database user: 'GRANT SELECT ON <object>(<column>) TO <user>;'."),
            262 => new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                $"SQL Server {code}",
                "CREATE TABLE permission denied in database.",
                "Grant CREATE TABLE permission to the database user ('GRANT CREATE TABLE TO <user>;'), or pre-create the table and set AutoCreate to false in configuration."),
            297 => new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                $"SQL Server {code}",
                "The user does not have permission to perform this action.",
                "Grant the required database permissions to the user or role."),

            // Connection / Network failures
            2 or 53 or 11001 or -1 or 10060 or 10061 or 10054 => new DbErrorDiagnosis(
                DbErrorKind.ConnectionFailed,
                "Connection",
                $"SQL Server {code}",
                "Could not establish a connection to SQL Server.",
                "Check that the server host/instance name is correct, SQL Server is running and accepting TCP/IP connections, and firewalls allow traffic on the port (default 1433)."),

            // Object / Column not found
            208 => new DbErrorDiagnosis(
                DbErrorKind.ObjectNotFound,
                "Object Not Found",
                $"SQL Server {code}",
                "Invalid object name. The referenced table or view does not exist in the database.",
                "Verify that the table or view exists in the target database and check schema qualification (e.g. 'dbo.TableName')."),
            207 => new DbErrorDiagnosis(
                DbErrorKind.ObjectNotFound,
                "Object Not Found",
                $"SQL Server {code}",
                "Invalid column name.",
                "Verify column names in the report SQL query against the database table schema."),

            // Snapshot isolation
            3952 or 3951 => new DbErrorDiagnosis(
                DbErrorKind.FeatureDisabled,
                "Configuration",
                $"SQL Server {code}",
                "Snapshot isolation transaction failed because ALLOW_SNAPSHOT_ISOLATION is disabled for this database.",
                "Enable snapshot isolation on the database ('ALTER DATABASE [DbName] SET ALLOW_SNAPSHOT_ISOLATION ON;') or set report consistency to 'none'."),

            // Timeout
            -2 or 30 => new DbErrorDiagnosis(
                DbErrorKind.Timeout,
                "Timeout",
                $"SQL Server {code}",
                "Execution timeout expired. The database command did not finish within the configured timeout.",
                "Increase CommandTimeoutSeconds in the report definition or optimize the query."),

            // Constraint violations
            2627 or 2601 => new DbErrorDiagnosis(
                DbErrorKind.ConstraintViolation,
                "Constraint Violation",
                $"SQL Server {code}",
                "Violation of unique constraint or unique index.",
                "A row with the same key already exists."),
            547 => new DbErrorDiagnosis(
                DbErrorKind.ConstraintViolation,
                "Constraint Violation",
                $"SQL Server {code}",
                "Foreign key or check constraint violation.",
                "Verify that referenced parent rows exist and check constraint conditions are satisfied."),

            // Concurrency
            1205 => new DbErrorDiagnosis(
                DbErrorKind.ConcurrencyConflict,
                "Concurrency",
                $"SQL Server {code}",
                "Transaction was deadlocked on lock resources and chosen as deadlock victim.",
                "Retry the operation, or review transaction isolation and query access patterns."),

            _ => null,
        };
    }

    /// <summary>
    /// Classifies Oracle provider exceptions by Oracle error number (ORA-XXXXX).
    /// </summary>
    private static DbErrorDiagnosis? ClassifyOracle(DbException ex)
    {
        var number = IntProperty(ex, "Number");
        if (!number.HasValue) return null;

        var code = $"ORA-{number.Value:D5}";
        return number.Value switch
        {
            // Authentication
            1017 => new DbErrorDiagnosis(
                DbErrorKind.AuthenticationFailed,
                "Authentication",
                code,
                "Invalid username/password; logon denied.",
                "Check the User Id and Password in the connection string or connection factory."),
            28000 => new DbErrorDiagnosis(
                DbErrorKind.AuthenticationFailed,
                "Authentication",
                code,
                "The Oracle database account is locked.",
                "Unlock the database account: 'ALTER USER <username> ACCOUNT UNLOCK;'."),
            28001 => new DbErrorDiagnosis(
                DbErrorKind.AuthenticationFailed,
                "Authentication",
                code,
                "The Oracle password has expired.",
                "Reset the password for the database user: 'ALTER USER <username> IDENTIFIED BY <new_password>;'."),
            1045 => new DbErrorDiagnosis(
                DbErrorKind.AuthenticationFailed,
                "Authentication",
                code,
                "User lacks CREATE SESSION privilege; logon denied.",
                "Grant CREATE SESSION privilege to the user: 'GRANT CREATE SESSION TO <username>;'."),

            // Permissions / Access denied / Missing tables
            942 => new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                code,
                "Table or view does not exist, or the user lacks SELECT privilege on it.",
                "In Oracle, ORA-00942 occurs both when a table is missing AND when the user lacks SELECT privileges. Verify that the table exists and that SELECT permission is granted: 'GRANT SELECT ON <schema>.<table> TO <user>;'."),
            1031 => new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                code,
                "Insufficient privileges.",
                "Grant the necessary privileges (such as SELECT, CREATE TABLE, or CREATE SEQUENCE) to the database user."),
            1950 => new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                code,
                "No privileges on tablespace.",
                "Grant tablespace quota to the user: 'ALTER USER <username> QUOTA UNLIMITED ON <tablespace>;'."),

            // Connection / TNS
            12154 => new DbErrorDiagnosis(
                DbErrorKind.ConnectionFailed,
                "Connection",
                code,
                "TNS: could not resolve the connect identifier specified.",
                "Check the TNS name, Easy Connect string (host:port/service_name), or tnsnames.ora configuration."),
            12170 => new DbErrorDiagnosis(
                DbErrorKind.ConnectionFailed,
                "Connection",
                code,
                "TNS: Connect timeout occurred.",
                "Check network connectivity, host address, and firewall settings between the host and Oracle server."),
            12541 => new DbErrorDiagnosis(
                DbErrorKind.ConnectionFailed,
                "Connection",
                code,
                "TNS: no listener.",
                "Verify that the Oracle database listener process is running on the target database host and port."),
            12514 => new DbErrorDiagnosis(
                DbErrorKind.ConnectionFailed,
                "Connection",
                code,
                "TNS: listener does not currently know of service requested.",
                "Check that the database service name in the connection string matches a service registered with the listener."),
            12545 => new DbErrorDiagnosis(
                DbErrorKind.ConnectionFailed,
                "Connection",
                code,
                "Connect failed because target host or object does not exist.",
                "Verify the database host name, IP address, and port in the connection string."),

            // Syntax / Identifier
            904 => new DbErrorDiagnosis(
                DbErrorKind.SyntaxOrSchemaError,
                "Syntax",
                code,
                "Invalid identifier (unknown column or alias).",
                "Verify column names, aliases, and expressions in the report SQL query."),

            // Timeout
            1013 => new DbErrorDiagnosis(
                DbErrorKind.Timeout,
                "Timeout",
                code,
                "User requested cancel of current operation (timeout).",
                "The database command timed out. Increase CommandTimeoutSeconds or optimize the query."),

            // Constraints
            1 => new DbErrorDiagnosis(
                DbErrorKind.ConstraintViolation,
                "Constraint Violation",
                code,
                "Unique constraint violated.",
                "A duplicate key was inserted for a unique constraint or primary key index."),
            2291 or 2292 => new DbErrorDiagnosis(
                DbErrorKind.ConstraintViolation,
                "Constraint Violation",
                code,
                "Integrity constraint / foreign key violated.",
                "Check parent/child foreign key relationships."),

            _ => null,
        };
    }

    /// <summary>
    /// Classifies PostgreSQL provider exceptions by SQLSTATE code.
    /// </summary>
    private static DbErrorDiagnosis? ClassifyPostgres(DbException ex)
    {
        var sqlState = GetSqlState(ex);
        if (string.IsNullOrWhiteSpace(sqlState)) return null;

        var code = $"SQLSTATE {sqlState}";
        if (string.Equals(sqlState, "28P01", StringComparison.Ordinal)
            || string.Equals(sqlState, "28000", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.AuthenticationFailed,
                "Authentication",
                code,
                "Password authentication failed for user.",
                "Check the username and password in the connection string or connection factory, and verify the user exists in PostgreSQL.");
        }

        if (string.Equals(sqlState, "42501", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                code,
                "Permission denied for table, schema, database, or sequence.",
                "Grant SELECT, INSERT, UPDATE, or CREATE permissions to the database user/role: 'GRANT SELECT ON ALL TABLES IN SCHEMA public TO <user>;' or 'GRANT CREATE ON SCHEMA public TO <user>;'.");
        }

        if (sqlState.StartsWith("08", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.ConnectionFailed,
                "Connection",
                code,
                "Could not connect to PostgreSQL server.",
                "Check that PostgreSQL is running, listening on the configured host/port, and that pg_hba.conf allows connections from this client host.");
        }

        if (string.Equals(sqlState, "42P01", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.ObjectNotFound,
                "Object Not Found",
                code,
                "Undefined table. The referenced table does not exist in the search_path.",
                "Verify table name spelling and schema qualification (e.g. 'public.table_name').");
        }

        if (string.Equals(sqlState, "42703", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.ObjectNotFound,
                "Object Not Found",
                code,
                "Undefined column.",
                "Verify column names in the report SQL query against the table schema.");
        }

        if (string.Equals(sqlState, "42601", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.SyntaxOrSchemaError,
                "Syntax",
                code,
                "Syntax error in SQL statement.",
                "Check the SQL query for PostgreSQL syntax compatibility.");
        }

        if (string.Equals(sqlState, "57014", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.Timeout,
                "Timeout",
                code,
                "Query canceled due to statement timeout.",
                "Increase CommandTimeoutSeconds in the report definition or optimize the query.");
        }

        if (string.Equals(sqlState, "23505", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.ConstraintViolation,
                "Constraint Violation",
                code,
                "Unique constraint violation.",
                "A row with the specified unique key already exists.");
        }

        if (string.Equals(sqlState, "23503", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.ConstraintViolation,
                "Constraint Violation",
                code,
                "Foreign key constraint violation.",
                "Check referenced parent/child rows.");
        }

        if (string.Equals(sqlState, "23514", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.ConstraintViolation,
                "Constraint Violation",
                code,
                "Check constraint violation.",
                "Verify that row values satisfy the table check constraint.");
        }

        if (string.Equals(sqlState, "40001", StringComparison.Ordinal)
            || string.Equals(sqlState, "40P01", StringComparison.Ordinal))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.ConcurrencyConflict,
                "Concurrency",
                code,
                "Serialization failure or deadlock detected.",
                "Retry the transaction or review concurrent access patterns.");
        }

        return null;
    }

    /// <summary>
    /// Classifies SQLite provider exceptions by extended or base error code.
    /// </summary>
    private static DbErrorDiagnosis? ClassifySqlite(DbException ex)
    {
        var extCode = IntProperty(ex, "SqliteExtendedErrorCode") ?? IntProperty(ex, "SqliteErrorCode");
        if (!extCode.HasValue) return null;

        var codeVal = extCode.Value;
        return codeVal switch
        {
            // Can't open file / path
            14 or 526 or 1038 => new DbErrorDiagnosis(
                DbErrorKind.ConnectionFailed,
                "Connection",
                $"SQLITE_CANTOPEN ({codeVal})",
                "Unable to open SQLite database file.",
                "Check that the database file path exists and that the application process has read/write filesystem permissions to the file and its parent folder."),

            // Readonly database
            8 or 1032 or 1288 or 1544 => new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                $"SQLITE_READONLY ({codeVal})",
                "Attempt to write to a read-only SQLite database.",
                "Check filesystem permissions on the database file and directory; ensure the file is not marked read-only."),

            // Auth callback denied
            23 => new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                $"SQLITE_AUTH ({codeVal})",
                "Authorization denied by SQLite authorizer callback.",
                "Check SQLite authorization hook configuration."),

            // Busy / Locked
            5 or 517 or 261 => new DbErrorDiagnosis(
                DbErrorKind.ConcurrencyConflict,
                "Concurrency",
                $"SQLITE_BUSY ({codeVal})",
                "SQLite database file is locked or busy.",
                "The database is locked by concurrent writers. Consider enabling WAL mode ('PRAGMA journal_mode=WAL;') or reducing lock contention."),
            6 or 518 => new DbErrorDiagnosis(
                DbErrorKind.ConcurrencyConflict,
                "Concurrency",
                $"SQLITE_LOCKED ({codeVal})",
                "SQLite database table is locked.",
                "A transaction or lock is currently held on this table."),

            // Constraints
            1555 or 2067 or 19 => new DbErrorDiagnosis(
                DbErrorKind.ConstraintViolation,
                "Constraint Violation",
                $"SQLITE_CONSTRAINT ({codeVal})",
                "SQLite constraint or primary key violation.",
                "A duplicate key was inserted for a unique constraint or primary key."),

            // Schema error
            1 => new DbErrorDiagnosis(
                DbErrorKind.SyntaxOrSchemaError,
                "Syntax",
                $"SQLITE_ERROR ({codeVal})",
                "SQL error or missing database table/column.",
                "Check SQL syntax and verify that referenced tables exist in the SQLite database."),

            _ => null,
        };
    }

    /// <summary>
    /// Classifies generic or wrapped exceptions using type inspection and keyword matching.
    /// </summary>
    private static DbErrorDiagnosis ClassifyGeneric(Exception ex)
    {
        if (ex is SocketException || ex.InnerException is SocketException)
        {
            return new DbErrorDiagnosis(
                DbErrorKind.ConnectionFailed,
                "Connection",
                "SocketException",
                "Network socket error while connecting to database host.",
                "Check network connectivity, host address, and firewall configuration.");
        }

        if (ex is TimeoutException || ex is OperationCanceledException)
        {
            return new DbErrorDiagnosis(
                DbErrorKind.Timeout,
                "Timeout",
                null,
                "Database operation timed out.",
                "Increase the configured command timeout or optimize the query.");
        }

        var message = ex.Message ?? "";

        if (ContainsAny(message, "login failed", "authentication failed", "password authentication", "logon denied", "invalid username/password"))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.AuthenticationFailed,
                "Authentication",
                null,
                "Database authentication failed.",
                "Check the username and password in the connection string or connection factory.");
        }

        if (ContainsAny(message, "permission denied", "insufficient privilege", "access is denied", "not authorized", "cannot create table"))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.PermissionDenied,
                "Permissions",
                null,
                "Database permission denied.",
                "Verify that the database user has been granted required permissions on the target database and objects.");
        }

        if (ContainsAny(message, "could not connect", "connection refused", "network unreachable", "server was not found", "no connection could be made", "unable to connect", "connection was closed"))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.ConnectionFailed,
                "Connection",
                null,
                "Could not establish database connection.",
                "Check that the database server is running, reachable over the network, and accepting connections.");
        }

        if (ContainsAny(message, "does not exist", "no such table", "invalid object name", "table or view does not exist", "undefined table"))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.ObjectNotFound,
                "Object Not Found",
                null,
                "Referenced database object (table or view) does not exist.",
                "Check table and view names in the query, and verify schema qualification.");
        }

        if (ContainsAny(message, "timeout", "timed out"))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.Timeout,
                "Timeout",
                null,
                "Database command timed out.",
                "Increase the command timeout or optimize query execution.");
        }

        if (ContainsAny(message, "syntax error", "incorrect syntax", "unrecognized token", "invalid identifier"))
        {
            return new DbErrorDiagnosis(
                DbErrorKind.SyntaxOrSchemaError,
                "Syntax",
                null,
                "SQL syntax or identifier error.",
                "Check the report SQL query syntax.");
        }

        return new DbErrorDiagnosis(
            DbErrorKind.Unclassified,
            "Database Error",
            null,
            message.Length > 0 ? message : "An unclassified database error occurred.",
            "Inspect the inner exception details and database connection configuration.");
    }

    /// <summary>
    /// Unwraps an exception chain to find the underlying <see cref="DbException"/>, if present.
    /// </summary>
    /// <param name="exception">The exception to inspect.</param>
    /// <returns>The inner or direct <see cref="DbException"/>, or <see langword="null"/>.</returns>
    public static DbException? UnwrapDbException(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is DbException dbEx) return dbEx;
            if (exception is AggregateException ae && ae.InnerExceptions.Count > 0)
            {
                foreach (var inner in ae.InnerExceptions)
                {
                    var found = UnwrapDbException(inner);
                    if (found is not null) return found;
                }
            }
            exception = exception.InnerException;
        }
        return null;
    }

    /// <summary>
    /// Reads the standard SQLSTATE string from a provider exception when present.
    /// </summary>
    private static string? GetSqlState(DbException exception)
    {
        if (!string.IsNullOrEmpty(exception.SqlState))
            return exception.SqlState;

        var getter = StringGetters.GetOrAdd((exception.GetType(), "SqlState"), static key =>
        {
            var property = key.Type.GetProperty(key.Property, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || property.PropertyType != typeof(string) || !property.CanRead)
                return null;
            return ex => (string?)property.GetValue(ex);
        });
        return getter?.Invoke(exception);
    }

    /// <summary>
    /// Reads an integer property from a provider exception when the property exists.
    /// </summary>
    /// <param name="exception">The exception whose provider-specific details are being classified or logged.</param>
    /// <param name="name">The public integer property name exposed by the provider exception.</param>
    /// <returns>The property value, or <see langword="null"/> when the property is absent, unreadable, or not an <see cref="int"/>.</returns>
    /// <remarks>Caches a reflection getter per exception type and property name.</remarks>
    private static int? IntProperty(DbException exception, string name)
    {
        var getter = IntGetters.GetOrAdd((exception.GetType(), name), static key =>
        {
            var property = key.Type.GetProperty(key.Property, BindingFlags.Public | BindingFlags.Instance);
            if (property is null || property.PropertyType != typeof(int) || !property.CanRead)
                return null;
            return ex => (int?)property.GetValue(ex);
        });
        return getter?.Invoke(exception);
    }

    private static bool ContainsAny(string text, params string[] phrases)
    {
        foreach (var phrase in phrases)
        {
            if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
