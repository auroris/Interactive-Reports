using Microsoft.Data.Sqlite;

namespace WindowsAuthentication
{
    internal static class SampleData
    {
        public static async Task EnsureCreated(string connectionString)
        {
            // Source data belongs to the host. The reporting library creates its configured
            // persistence tables on first use, but it does not create the ORDERS dataset.
            // Replace this setup with an existing database in a real integration.
            using (SqliteConnection connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    // This runs at every startup. Stable primary keys and INSERT OR IGNORE
                    // keep restarts from duplicating orders or overwriting existing rows.
                    // Saved documents and administrator grants are left intact.
                    command.CommandText =
                        "CREATE TABLE IF NOT EXISTS ORDERS (" +
                        "ORDER_ID INTEGER PRIMARY KEY, CUSTOMER TEXT NOT NULL, AMOUNT REAL NOT NULL); " +
                        "INSERT OR IGNORE INTO ORDERS VALUES " +
                        "(1, 'Alice', 120.50), (2, 'Benoit', 75.00), (3, 'Cara', 240.00);";
                    await command.ExecuteNonQueryAsync();
                }
            }
        }
    }
}
