using Microsoft.Data.Sqlite;

namespace Workbench;

/// <summary>Creates deterministic sample data so every workbench checkout demonstrates the same reports.</summary>
public static class SampleData
{
    private static readonly string[] Customers =
    [
        "Acme Corp", "Globex", "Initech", "Umbrella Group", "Stark Industries", "Wayne Enterprises",
        "Tyrell Corp", "Wonka Industries", "Cyberdyne Systems", "Aperture Science", "Soylent Corp", "Hooli",
    ];

    private static readonly string[] Regions = ["NORTH", "SOUTH", "EAST", "WEST"];
    private static readonly string[] Statuses = ["NEW", "PENDING", "SHIPPED", "CANCELLED"];

    /// <summary>
    /// Creates the sample SQLite database and inserts 500 deterministic orders when the table is empty.
    /// </summary>
    /// <param name="dbPath">The SQLite database-file path to initialize.</param>
    /// <remarks>Creates the parent directory and database file when absent. Existing non-empty order data is left unchanged.</remarks>
    public static void EnsureSeeded(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using (var create = conn.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS ORDERS (
                    ORDER_ID   INTEGER PRIMARY KEY,
                    CUSTOMER   TEXT    NOT NULL,
                    REGION     TEXT    NOT NULL,
                    STATUS     TEXT    NOT NULL,
                    AMOUNT     NUMERIC NOT NULL,
                    ORDER_DATE TEXT    NOT NULL,
                    NOTES      TEXT    NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        using (var count = conn.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM ORDERS";
            if (Convert.ToInt64(count.ExecuteScalar()) > 0) return;
        }

        var rng = new Random(42);
        var start = new DateOnly(2025, 1, 1);

        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO ORDERS (CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES)
            VALUES (@customer, @region, @status, @amount, @date, @notes)
            """;
        var pCustomer = insert.Parameters.Add("@customer", SqliteType.Text);
        var pRegion = insert.Parameters.Add("@region", SqliteType.Text);
        var pStatus = insert.Parameters.Add("@status", SqliteType.Text);
        var pAmount = insert.Parameters.Add("@amount", SqliteType.Real);
        var pDate = insert.Parameters.Add("@date", SqliteType.Text);
        var pNotes = insert.Parameters.Add("@notes", SqliteType.Text);

        for (var i = 0; i < 500; i++)
        {
            pCustomer.Value = Customers[rng.Next(Customers.Length)];
            pRegion.Value = Regions[rng.Next(Regions.Length)];
            pStatus.Value = Statuses[rng.Next(Statuses.Length)];
            pAmount.Value = Math.Round(rng.NextDouble() * 24_990 + 10, 2);
            pDate.Value = start.AddDays(rng.Next(550)).ToString("yyyy-MM-dd");
            pNotes.Value = rng.Next(4) == 0 ? (object)$"Priority handling #{i}" : DBNull.Value;
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
