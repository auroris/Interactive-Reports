// Workbench-only CRUD endpoints for the ORDERS sample table. They exist so crud.html can show a
// host-owned editor driven by the report's ir-edit / ir-create events; nothing here belongs to the
// Interactive Reports packages, which never write application data.

using Microsoft.Data.Sqlite;

namespace Workbench;

/// <summary>One ORDERS row as the crud.html editor exchanges it.</summary>
public sealed record OrderRecord(
    long? OrderId,
    string? Customer,
    string? Region,
    string? Status,
    decimal? Amount,
    string? OrderDate,
    string? Notes);

/// <summary>Maps the sample's minimal order CRUD API under <c>/api/orders</c>.</summary>
public static class OrdersCrud
{
    private static readonly string[] Regions = ["NORTH", "SOUTH", "EAST", "WEST"];
    private static readonly string[] Statuses = ["NEW", "PENDING", "SHIPPED", "CANCELLED"];

    /// <summary>Registers GET/POST/PUT/DELETE endpoints against the sample SQLite database.</summary>
    /// <param name="app">The host route builder.</param>
    /// <param name="connectionString">The sample database connection string.</param>
    /// <returns>The same route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapWorkbenchOrdersCrud(this IEndpointRouteBuilder app, string connectionString)
    {
        var group = app.MapGroup("/api/orders").WithTags("Workbench orders CRUD");

        group.MapGet("/{id:long}", async (long id, CancellationToken ct) =>
        {
            var record = await Find(connectionString, id, ct);
            return record is null ? Results.NotFound() : Results.Ok(record);
        });

        group.MapPost("/", async (OrderRecord input, CancellationToken ct) =>
        {
            if (Validate(input) is { } problem) return problem;

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var insert = conn.CreateCommand();
            insert.CommandText = """
                INSERT INTO ORDERS (CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES)
                VALUES (@customer, @region, @status, @amount, @date, @notes);
                SELECT last_insert_rowid();
                """;
            Bind(insert, input);
            var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
            return Results.Created($"/api/orders/{id}", await Find(connectionString, id, ct));
        });

        group.MapPut("/{id:long}", async (long id, OrderRecord input, CancellationToken ct) =>
        {
            if (Validate(input) is { } problem) return problem;

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var update = conn.CreateCommand();
            update.CommandText = """
                UPDATE ORDERS
                SET CUSTOMER = @customer, REGION = @region, STATUS = @status,
                    AMOUNT = @amount, ORDER_DATE = @date, NOTES = @notes
                WHERE ORDER_ID = @id
                """;
            Bind(update, input);
            update.Parameters.AddWithValue("@id", id);
            if (await update.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            return Results.Ok(await Find(connectionString, id, ct));
        });

        group.MapDelete("/{id:long}", async (long id, CancellationToken ct) =>
        {
            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var delete = conn.CreateCommand();
            delete.CommandText = "DELETE FROM ORDERS WHERE ORDER_ID = @id";
            delete.Parameters.AddWithValue("@id", id);
            return await delete.ExecuteNonQueryAsync(ct) == 0 ? Results.NotFound() : Results.NoContent();
        });

        return app;
    }

    private static async Task<OrderRecord?> Find(string connectionString, long id, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var select = conn.CreateCommand();
        select.CommandText = """
            SELECT ORDER_ID, CUSTOMER, REGION, STATUS, AMOUNT, ORDER_DATE, NOTES
            FROM ORDERS WHERE ORDER_ID = @id
            """;
        select.Parameters.AddWithValue("@id", id);
        await using var reader = await select.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new OrderRecord(
            OrderId: reader.GetInt64(0),
            Customer: reader.GetString(1),
            Region: reader.GetString(2),
            Status: reader.GetString(3),
            Amount: Convert.ToDecimal(reader.GetValue(4)),
            OrderDate: reader.GetString(5),
            Notes: reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static IResult? Validate(OrderRecord input)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(input.Customer) || input.Customer.Trim().Length > 200)
            errors["customer"] = ["Customer is required and must be at most 200 characters."];
        if (!Regions.Contains(input.Region, StringComparer.OrdinalIgnoreCase))
            errors["region"] = [$"Region must be one of {string.Join(", ", Regions)}."];
        if (!Statuses.Contains(input.Status, StringComparer.OrdinalIgnoreCase))
            errors["status"] = [$"Status must be one of {string.Join(", ", Statuses)}."];
        if (input.Amount is null or < 0 or > 1_000_000_000)
            errors["amount"] = ["Amount must be a non-negative number."];
        if (!DateOnly.TryParseExact(input.OrderDate, "yyyy-MM-dd", out _))
            errors["orderDate"] = ["Order date must be an ISO date (yyyy-MM-dd)."];
        if (input.Notes is { Length: > 2000 })
            errors["notes"] = ["Notes must be at most 2000 characters."];
        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static void Bind(SqliteCommand command, OrderRecord input)
    {
        command.Parameters.AddWithValue("@customer", input.Customer!.Trim());
        command.Parameters.AddWithValue("@region", input.Region!.ToUpperInvariant());
        command.Parameters.AddWithValue("@status", input.Status!.ToUpperInvariant());
        command.Parameters.Add("@amount", SqliteType.Real).Value = (double)input.Amount!.Value;
        command.Parameters.AddWithValue("@date", input.OrderDate!);
        command.Parameters.AddWithValue("@notes",
            string.IsNullOrWhiteSpace(input.Notes) ? DBNull.Value : input.Notes.Trim());
    }
}
