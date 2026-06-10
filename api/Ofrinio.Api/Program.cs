using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);

var allowedOrigins = builder.Configuration
    .GetSection("AllowedOrigins")
    .Get<string[]>()
    ?? ["https://martin9020.github.io", "http://localhost:4200"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

app.MapGet("/api/health", () => Results.Ok(new
{
    ok = true,
    app = "ofrinio-api",
    databaseConfigured = !string.IsNullOrWhiteSpace(GetConnectionString(app.Configuration))
}));

app.MapGet("/api/availability", async (IConfiguration configuration) =>
{
    var connectionString = GetConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Ok(FallbackAvailability());
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    const string sql = """
        select [Date], [Status]
        from dbo.Availability
        where [Date] >= dateadd(month, -1, cast(sysdatetimeoffset() as date))
        order by [Date];
        """;

    await using var command = new SqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<AvailabilityDto>();

    while (await reader.ReadAsync())
    {
        rows.Add(new AvailabilityDto(
            reader.GetDateTime(0).ToString("yyyy-MM-dd"),
            NormalizeStatus(reader.GetString(1))));
    }

    return Results.Ok(rows);
});

app.MapPost("/api/booking-requests", async (
    BookingRequestDto request,
    IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) ||
        string.IsNullOrWhiteSpace(request.Phone) ||
        string.IsNullOrWhiteSpace(request.RequestedDates))
    {
        return Results.BadRequest(new { error = "Name, phone and requestedDates are required." });
    }

    var connectionString = GetConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Accepted(value: new
        {
            saved = false,
            message = "Database is not configured. Request accepted in demo mode."
        });
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();

    const string sql = """
        insert into dbo.BookingRequests ([Name], [Phone], [RequestedDates], [Message], [Source])
        values (@Name, @Phone, @RequestedDates, @Message, @Source);
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@Name", request.Name.Trim());
    command.Parameters.AddWithValue("@Phone", request.Phone.Trim());
    command.Parameters.AddWithValue("@RequestedDates", request.RequestedDates.Trim());
    command.Parameters.AddWithValue("@Message", (object?)request.Message?.Trim() ?? DBNull.Value);
    command.Parameters.AddWithValue("@Source", "github-pages-angular");
    await command.ExecuteNonQueryAsync();

    return Results.Created("/api/booking-requests", new { saved = true });
});

app.Run();

static string? GetConnectionString(IConfiguration configuration)
{
    return configuration.GetConnectionString("OfrinioSql")
        ?? Environment.GetEnvironmentVariable("AZURE_SQL_CONNECTION_STRING");
}

static string NormalizeStatus(string value)
{
    return value.Trim().ToLowerInvariant() switch
    {
        "booked" or "reserved" or "confirmed" => "booked",
        "pending" or "request" => "pending",
        _ => "free"
    };
}

static AvailabilityDto[] FallbackAvailability()
{
    return
    [
        new("2026-06-20", "pending"),
        new("2026-06-21", "pending"),
        new("2026-07-05", "booked"),
        new("2026-07-06", "booked"),
        new("2026-07-07", "booked"),
        new("2026-07-08", "booked"),
        new("2026-08-10", "booked"),
        new("2026-08-11", "booked"),
        new("2026-08-12", "booked"),
        new("2026-09-02", "pending")
    ];
}

record AvailabilityDto(string Date, string Status);

record BookingRequestDto(
    string Name,
    string Phone,
    string RequestedDates,
    string? Message);
