using Oracle.ManagedDataAccess.Client;

namespace InteractiveReport.Core.Tests;

/// <summary>Scratch-schema helpers for the live Oracle store tests.</summary>
internal static class OracleTestSchema
{
    /// <summary>Whether the test user's session holds a system privilege such as CREATE TRIGGER.</summary>
    public static bool HasPrivilege(string connectionString, string privilege)
    {
        using var connection = new OracleConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM session_privs WHERE privilege = '{privilege}'";
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    /// <summary>
    /// Runs DROP statements, ignoring a missing table (ORA-00942) or sequence (ORA-02289) so a
    /// test can clean up before and after itself without knowing what the last run left.
    /// </summary>
    public static void Drop(string connectionString, params string[] statements)
    {
        using var connection = new OracleConnection(connectionString);
        connection.Open();
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                BEGIN
                    EXECUTE IMMEDIATE '{statement}';
                EXCEPTION WHEN OTHERS THEN
                    IF SQLCODE NOT IN (-942, -2289) THEN RAISE; END IF;
                END;
                """;
            command.ExecuteNonQuery();
        }
    }
}
