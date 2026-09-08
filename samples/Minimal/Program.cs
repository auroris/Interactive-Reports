using InteractiveReport.AspNetCore;
using InteractiveReport.Client.Json;
using Microsoft.Data.Sqlite;

namespace Minimal
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

            // A shared in-memory database disappears when its last connection closes.
            // Keep this one open for the application's lifetime so each report request
            // can open its own connection and still find the sample rows.
            using (SqliteConnection database = new SqliteConnection(
                builder.Configuration.GetConnectionString("SampleDb")))
            {
                await database.OpenAsync();
                await CreateSampleData(database);

                // The engine reads report definitions and connection settings from configuration.
                // The JSON client supplies the browser viewer and the API it calls.
                // No SavedReports configuration is supplied, so this host needs no document store.
                builder.Services.AddInteractiveReports(builder.Configuration);
                builder.Services.AddInteractiveReportJson();

                WebApplication app = builder.Build();
                // Registering services alone does not expose HTTP routes. The host chooses
                // the prefix so reports can fit into an existing application's URL structure.
                app.MapInteractiveReportJson("/api/reports");
                app.MapGet("/", OpenReport);

                await app.RunAsync();
            }
        }

        private static async Task CreateSampleData(SqliteConnection database)
        {
            // These rows make the example runnable without an existing business database.
            // Creating source tables is the host's responsibility. An application with
            // an existing database would omit this method and supply its own report SQL.
            using (SqliteCommand command = database.CreateCommand())
            {
                command.CommandText =
                    "CREATE TABLE ORDERS (ORDER_ID INTEGER PRIMARY KEY, CUSTOMER TEXT NOT NULL, AMOUNT REAL NOT NULL); " +
                    "INSERT INTO ORDERS VALUES (1, 'Alice', 120.50), (2, 'Benoit', 75.00), (3, 'Cara', 240.00);";
                await command.ExecuteNonQueryAsync();
            }
        }

        private static IResult OpenReport()
        {
            // Reuse the packaged viewer so the example needs no separate HTML page.
            // The report permits anonymous access in appsettings.json; an authenticated
            // application would configure its identity middleware and remove that setting.
            return Results.Redirect("/api/reports/orders/view");
        }
    }
}
