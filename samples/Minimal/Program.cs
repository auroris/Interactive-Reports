using InteractiveReport.AspNetCore;
using InteractiveReport.Client.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// Sample data belongs to the host. Keep this connection open for the app's lifetime
// so report connections can read the same in-memory database.
await using var database = new SqliteConnection(builder.Configuration.GetConnectionString("SampleDb"));
await database.OpenAsync();
await using (var seed = database.CreateCommand())
{
    seed.CommandText = """
        CREATE TABLE ORDERS (ORDER_ID INTEGER PRIMARY KEY, CUSTOMER TEXT NOT NULL, AMOUNT REAL NOT NULL);
        INSERT INTO ORDERS VALUES (1, 'Alice', 120.50), (2, 'Benoit', 75.00), (3, 'Cara', 240.00);
        """;
    await seed.ExecuteNonQueryAsync();
}

builder.Services.AddInteractiveReports(builder.Configuration);
builder.Services.AddInteractiveReportJson();

var app = builder.Build();
app.MapInteractiveReportJson("/api/reports");
app.MapGet("/", () => Results.Redirect("/api/reports/orders/view"));

await app.RunAsync();
