using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
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
builder.Services.AddHttpClient();
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
    databaseConfigured = !string.IsNullOrWhiteSpace(GetConnectionString(app.Configuration)),
    adminConfigured = IsAdminConfigured(app.Configuration),
    supabaseImportConfigured = IsSupabaseImportConfigured(app.Configuration)
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
    await EnsureDatabaseSchema(connection);

    const string sql = """
        select [Date], [Status]
        from dbo.Availability
        where [Date] >= dateadd(month, -1, cast(sysdatetimeoffset() as date))
          and [Status] in ('booked', 'pending')
        order by [Date];
        """;

    await using var command = new SqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<AvailabilityDto>();

    while (await reader.ReadAsync())
    {
        rows.Add(new AvailabilityDto(
            reader.GetDateTime(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
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
    await EnsureDatabaseSchema(connection);

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

app.MapPost("/api/owner/login", async (AdminLoginRequest request, IConfiguration configuration) =>
{
    var connectionString = GetConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString) ||
        string.IsNullOrWhiteSpace(GetAdminTokenSecret(configuration)))
    {
        return Results.Problem(
            title: "Admin login is not configured.",
            detail: "Set AZURE_SQL_CONNECTION_STRING and OFRINIO_ADMIN_TOKEN_SECRET in the Azure API settings.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(request.Username) ||
        string.IsNullOrWhiteSpace(request.Password))
    {
        return Results.Unauthorized();
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await EnsureDatabaseSchema(connection);

    const string sql = """
        select [PasswordHash]
        from dbo.AdminUsers
        where [Username] = @Username
          and [IsActive] = 1;
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@Username", NormalizeUsername(request.Username));
    var passwordHash = await command.ExecuteScalarAsync() as string;

    if (string.IsNullOrWhiteSpace(passwordHash) ||
        !VerifyPassword(request.Password, passwordHash))
    {
        return Results.Unauthorized();
    }

    var expiresAt = DateTimeOffset.UtcNow.AddHours(12);
    return Results.Ok(new AdminLoginResponse(
        CreateAdminToken(configuration, expiresAt, NormalizeUsername(request.Username)),
        expiresAt));
});

app.MapPost("/api/owner/bootstrap-users", async (
    AdminBootstrapUsersRequest request,
    HttpRequest httpRequest,
    IConfiguration configuration) =>
{
    if (!IsAuthorizedBootstrap(httpRequest, configuration))
    {
        return Results.Unauthorized();
    }

    var connectionString = GetConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem(
            title: "Azure SQL is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (request.Users.Count == 0)
    {
        return Results.BadRequest(new { error = "At least one user is required." });
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await EnsureDatabaseSchema(connection);

    foreach (var user in request.Users)
    {
        if (string.IsNullOrWhiteSpace(user.Username) ||
            string.IsNullOrWhiteSpace(user.Password))
        {
            return Results.BadRequest(new { error = "Each user needs username and password." });
        }

        await UpsertAdminUser(
            connection,
            NormalizeUsername(user.Username),
            HashPassword(user.Password),
            user.DisplayName);
    }

    return Results.Ok(new { saved = request.Users.Count });
});

app.MapGet("/api/owner/me", (HttpRequest httpRequest, IConfiguration configuration) =>
{
    var identity = GetAuthorizedAdminIdentity(httpRequest, configuration);
    return identity is null
        ? Results.Unauthorized()
        : Results.Ok(new AdminMeResponse(identity));
});

app.MapGet("/api/owner/availability", async (HttpRequest httpRequest, IConfiguration configuration) =>
{
    if (!IsAuthorizedAdmin(httpRequest, configuration))
    {
        return Results.Unauthorized();
    }

    var connectionString = GetConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem(
            title: "Azure SQL is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await EnsureDatabaseSchema(connection);

    const string sql = """
        select [Date], [Status], [GuestName], [Phone], [Notes], [UpdatedAt]
        from dbo.Availability
        where [Status] in ('booked', 'pending')
        order by [Date];
        """;

    await using var command = new SqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var rows = new List<AdminAvailabilityDto>();

    while (await reader.ReadAsync())
    {
        rows.Add(ReadAdminAvailability(reader));
    }

    return Results.Ok(rows);
});

app.MapGet("/api/owner/settings", async (HttpRequest httpRequest, IConfiguration configuration) =>
{
    if (!IsAuthorizedAdmin(httpRequest, configuration))
    {
        return Results.Unauthorized();
    }

    var connectionString = GetConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem(
            title: "Azure SQL is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await EnsureDatabaseSchema(connection);

    return Results.Ok(new AdminSettingsResponse(
        await GetSupabaseSyncEnabled(connection, configuration)));
});

app.MapPut("/api/owner/settings", async (
    AdminSettingsRequest request,
    HttpRequest httpRequest,
    IConfiguration configuration) =>
{
    if (!IsAuthorizedAdmin(httpRequest, configuration))
    {
        return Results.Unauthorized();
    }

    var connectionString = GetConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem(
            title: "Azure SQL is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await EnsureDatabaseSchema(connection);
    await SetAppSetting(connection, "SupabaseSyncEnabled", request.SupabaseSyncEnabled ? "true" : "false");

    return Results.Ok(new AdminSettingsResponse(request.SupabaseSyncEnabled));
});

app.MapPut("/api/owner/availability/range", async (
    AdminAvailabilityRangeRequest request,
    HttpRequest httpRequest,
    IConfiguration configuration) =>
{
    if (!IsAuthorizedAdmin(httpRequest, configuration))
    {
        return Results.Unauthorized();
    }

    if (!TryParseDate(request.StartDate, out var startDate) ||
        !TryParseDate(request.EndDate, out var endDate))
    {
        return Results.BadRequest(new { error = "startDate and endDate must use yyyy-MM-dd." });
    }

    if (endDate < startDate)
    {
        return Results.BadRequest(new { error = "endDate must be on or after startDate." });
    }

    var status = NormalizeStatus(request.Status);
    var dayCount = endDate.DayNumber - startDate.DayNumber + 1;
    if (dayCount > 370)
    {
        return Results.BadRequest(new { error = "Date ranges longer than 370 days are not allowed." });
    }

    var connectionString = GetConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem(
            title: "Azure SQL is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await EnsureDatabaseSchema(connection);
    await using var transaction = await connection.BeginTransactionAsync();

    if (status == "free")
    {
        await DeleteAvailabilityRange(connection, transaction, startDate, endDate);
    }
    else
    {
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            await UpsertAvailability(
                connection,
                transaction,
                date,
                status,
                request.GuestName,
                request.Phone,
                request.Notes,
                "admin");
        }
    }

    await transaction.CommitAsync();
    return Results.Ok(new { saved = true, days = dayCount, status });
});

app.MapPost("/api/owner/import-supabase", async (
    SupabaseImportRequest? request,
    HttpRequest httpRequest,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory) =>
{
    if (!IsAuthorizedAdmin(httpRequest, configuration))
    {
        return Results.Unauthorized();
    }

    var connectionString = GetConnectionString(configuration);
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        return Results.Problem(
            title: "Azure SQL is not configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var imported = await LoadSupabaseAvailability(configuration, httpClientFactory.CreateClient());
    if (imported.Count == 0)
    {
        return Results.BadRequest(new { error = "No Supabase availability rows were found." });
    }

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await EnsureDatabaseSchema(connection);
    await using var transaction = await connection.BeginTransactionAsync();

    if (request?.ReplaceExisting == true)
    {
        await using var deleteCommand = new SqlCommand("delete from dbo.Availability;", connection, (SqlTransaction)transaction);
        await deleteCommand.ExecuteNonQueryAsync();
    }

    var saved = 0;
    foreach (var entry in imported)
    {
        if (entry.Status == "free")
        {
            continue;
        }

        await UpsertAvailability(
            connection,
            transaction,
            entry.Date,
            entry.Status,
            entry.GuestName,
            entry.Phone,
            entry.Notes,
            entry.Source);
        saved += 1;
    }

    await transaction.CommitAsync();
    return Results.Ok(new
    {
        imported = saved,
        source = imported.Any(entry => entry.Source == "supabase-reservations")
            ? "supabase-reservations"
            : "supabase-public-availability",
        replacedExisting = request?.ReplaceExisting == true
    });
});

app.Run();

static string? GetConnectionString(IConfiguration configuration)
{
    return FirstConfigured(
        Environment.GetEnvironmentVariable("AZURE_SQL_CONNECTION_STRING"),
        configuration.GetConnectionString("OfrinioSql"));
}

static string? GetBootstrapToken(IConfiguration configuration)
{
    return FirstConfigured(
        Environment.GetEnvironmentVariable("OFRINIO_ADMIN_BOOTSTRAP_TOKEN"),
        configuration["Admin:BootstrapToken"]);
}

static string? GetAdminTokenSecret(IConfiguration configuration)
{
    return FirstConfigured(
        Environment.GetEnvironmentVariable("OFRINIO_ADMIN_TOKEN_SECRET"),
        configuration["Admin:TokenSecret"]);
}

static bool IsAdminConfigured(IConfiguration configuration)
{
    return GetAllowedAdminEmails(configuration).Count > 0 ||
        (!string.IsNullOrWhiteSpace(GetConnectionString(configuration)) &&
         !string.IsNullOrWhiteSpace(GetAdminTokenSecret(configuration)));
}

static IReadOnlySet<string> GetAllowedAdminEmails(IConfiguration configuration)
{
    var configured = FirstConfigured(
        Environment.GetEnvironmentVariable("OFRINIO_ADMIN_EMAILS"),
        configuration["Admin:AllowedEmails"]);

    return (configured ?? string.Empty)
        .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static bool IsSupabaseImportConfigured(IConfiguration configuration)
{
    return !string.IsNullOrWhiteSpace(GetSupabaseUrl(configuration)) &&
        (!string.IsNullOrWhiteSpace(GetSupabaseAnonKey(configuration)) ||
         !string.IsNullOrWhiteSpace(GetSupabaseServiceRoleKey(configuration)));
}

static string? GetSupabaseUrl(IConfiguration configuration)
{
    return FirstConfigured(
        Environment.GetEnvironmentVariable("SUPABASE_URL"),
        configuration["Supabase:Url"],
        "https://hqmgnouwuastlsenalre.supabase.co");
}

static string? GetSupabaseAnonKey(IConfiguration configuration)
{
    return FirstConfigured(
        Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY"),
        configuration["Supabase:AnonKey"],
        "sb_publishable_TkdiVOTUPQqrkuY3UVPC1A_BYWj7KnC");
}

static string? GetSupabaseServiceRoleKey(IConfiguration configuration)
{
    return FirstConfigured(
        Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY"),
        configuration["Supabase:ServiceRoleKey"]);
}

static string? FirstConfigured(params string?[] values)
{
    return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}

static async Task EnsureDatabaseSchema(SqlConnection connection)
{
    const string sql = """
        if not exists (select * from sys.objects where object_id = object_id(N'[dbo].[Availability]') and type in (N'U'))
        begin
            create table dbo.Availability (
                [Date] date primary key,
                [Status] nvarchar(50) not null check ([Status] in ('free', 'booked', 'pending')),
                [GuestName] nvarchar(255) null,
                [Phone] nvarchar(50) null,
                [Notes] nvarchar(max) null,
                [Source] nvarchar(100) null,
                [UpdatedAt] datetime2 not null default sysutcdatetime()
            );
        end;

        if col_length('dbo.Availability', 'GuestName') is null
            alter table dbo.Availability add [GuestName] nvarchar(255) null;

        if col_length('dbo.Availability', 'Phone') is null
            alter table dbo.Availability add [Phone] nvarchar(50) null;

        if col_length('dbo.Availability', 'Notes') is null
            alter table dbo.Availability add [Notes] nvarchar(max) null;

        if col_length('dbo.Availability', 'Source') is null
            alter table dbo.Availability add [Source] nvarchar(100) null;

        if col_length('dbo.Availability', 'UpdatedAt') is null
            alter table dbo.Availability add [UpdatedAt] datetime2 not null constraint DF_Availability_UpdatedAt default sysutcdatetime();

        if not exists (select * from sys.objects where object_id = object_id(N'[dbo].[BookingRequests]') and type in (N'U'))
        begin
            create table dbo.BookingRequests (
                [Id] int identity(1,1) primary key,
                [Name] nvarchar(255) not null,
                [Phone] nvarchar(50) not null,
                [RequestedDates] nvarchar(1000) not null,
                [Message] nvarchar(max) null,
                [Source] nvarchar(50) not null,
                [CreatedAt] datetime2 default sysutcdatetime()
            );
        end;

        if not exists (select * from sys.objects where object_id = object_id(N'[dbo].[AdminUsers]') and type in (N'U'))
        begin
            create table dbo.AdminUsers (
                [Username] nvarchar(100) not null primary key,
                [PasswordHash] nvarchar(500) not null,
                [DisplayName] nvarchar(255) null,
                [IsActive] bit not null default 1,
                [CreatedAt] datetime2 not null default sysutcdatetime(),
                [UpdatedAt] datetime2 not null default sysutcdatetime()
            );
        end;

        if not exists (select * from sys.objects where object_id = object_id(N'[dbo].[AppSettings]') and type in (N'U'))
        begin
            create table dbo.AppSettings (
                [SettingKey] nvarchar(100) not null primary key,
                [SettingValue] nvarchar(1000) not null,
                [UpdatedAt] datetime2 not null default sysutcdatetime()
            );
        end;
        """;

    await using var command = new SqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static async Task<bool> GetSupabaseSyncEnabled(SqlConnection connection, IConfiguration configuration)
{
    var saved = await GetAppSetting(connection, "SupabaseSyncEnabled");
    if (!string.IsNullOrWhiteSpace(saved))
    {
        return string.Equals(saved, "true", StringComparison.OrdinalIgnoreCase);
    }

    var configured = FirstConfigured(
        Environment.GetEnvironmentVariable("SUPABASE_SYNC_ENABLED"),
        configuration["Supabase:SyncEnabled"]);

    return string.Equals(configured, "true", StringComparison.OrdinalIgnoreCase);
}

static async Task<string?> GetAppSetting(SqlConnection connection, string key)
{
    const string sql = """
        select [SettingValue]
        from dbo.AppSettings
        where [SettingKey] = @SettingKey;
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@SettingKey", key);
    return await command.ExecuteScalarAsync() as string;
}

static async Task SetAppSetting(SqlConnection connection, string key, string value)
{
    const string sql = """
        merge dbo.AppSettings as target
        using (values (@SettingKey, @SettingValue)) as source ([SettingKey], [SettingValue])
        on target.[SettingKey] = source.[SettingKey]
        when matched then
            update set
                [SettingValue] = source.[SettingValue],
                [UpdatedAt] = sysutcdatetime()
        when not matched then
            insert ([SettingKey], [SettingValue], [UpdatedAt])
            values (source.[SettingKey], source.[SettingValue], sysutcdatetime());
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@SettingKey", key);
    command.Parameters.AddWithValue("@SettingValue", value);
    await command.ExecuteNonQueryAsync();
}

static string NormalizeUsername(string username)
{
    return username.Trim().ToLowerInvariant();
}

static string NormalizeStatus(string? value)
{
    return (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "booked" or "reserved" or "confirmed" or "потвърдена" => "booked",
        "pending" or "request" or "чакаща" => "pending",
        _ => "free"
    };
}

static bool TryParseDate(string? value, out DateOnly date)
{
    return DateOnly.TryParseExact(
        value,
        "yyyy-MM-dd",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out date);
}

static bool FixedTimeEquals(string provided, string expected)
{
    var providedBytes = Encoding.UTF8.GetBytes(provided);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return providedBytes.Length == expectedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
}

static string HashPassword(string password)
{
    const int iterations = 210_000;
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(password),
        salt,
        iterations,
        HashAlgorithmName.SHA256,
        32);

    return $"pbkdf2-sha256:{iterations}:{Base64UrlEncode(salt)}:{Base64UrlEncode(hash)}";
}

static bool VerifyPassword(string password, string encodedHash)
{
    var parts = encodedHash.Split(':', 4);
    if (parts.Length != 4 ||
        !string.Equals(parts[0], "pbkdf2-sha256", StringComparison.Ordinal) ||
        !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) ||
        iterations < 100_000)
    {
        return false;
    }

    try
    {
        var salt = Base64UrlDecode(parts[2]);
        var expectedHash = Base64UrlDecode(parts[3]);
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
    catch (FormatException)
    {
        return false;
    }
}

static string CreateAdminToken(IConfiguration configuration, DateTimeOffset expiresAt, string username)
{
    var expiresUnix = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    var encodedUsername = Base64UrlEncode(Encoding.UTF8.GetBytes(username));
    var signature = SignAdminToken(configuration, expiresUnix, encodedUsername);
    return $"{expiresUnix}.{encodedUsername}.{Base64UrlEncode(signature)}";
}

static bool IsAuthorizedAdmin(HttpRequest request, IConfiguration configuration)
{
    return GetAuthorizedAdminIdentity(request, configuration) is not null;
}

static string? GetAuthorizedAdminIdentity(HttpRequest request, IConfiguration configuration)
{
    return GetAuthorizedAdminEmail(request, configuration) ??
        GetAuthorizedAdminTokenUsername(request, configuration);
}

static string? GetAuthorizedAdminEmail(HttpRequest request, IConfiguration configuration)
{
    var allowedEmails = GetAllowedAdminEmails(configuration);
    if (allowedEmails.Count == 0)
    {
        return null;
    }

    var email = request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].ToString();
    if (string.IsNullOrWhiteSpace(email))
    {
        email = request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].ToString();
    }

    return !string.IsNullOrWhiteSpace(email) && allowedEmails.Contains(email)
        ? email
        : null;
}

static string? GetAuthorizedAdminTokenUsername(HttpRequest request, IConfiguration configuration)
{
    if (string.IsNullOrWhiteSpace(GetAdminTokenSecret(configuration)))
    {
        return null;
    }

    var header = request.Headers.Authorization.ToString();
    if (string.IsNullOrWhiteSpace(header) ||
        !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    var token = header["Bearer ".Length..].Trim();
    var parts = token.Split('.', 3);
    if (parts.Length != 3 ||
        !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var expiresUnix) ||
        expiresUnix < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    {
        return null;
    }

    var expectedSignature = Base64UrlEncode(SignAdminToken(configuration, parts[0], parts[1]));
    return FixedTimeEquals(parts[2], expectedSignature)
        ? Encoding.UTF8.GetString(Base64UrlDecode(parts[1]))
        : null;
}

static bool IsAuthorizedBootstrap(HttpRequest request, IConfiguration configuration)
{
    var bootstrapToken = GetBootstrapToken(configuration);
    if (string.IsNullOrWhiteSpace(bootstrapToken))
    {
        return false;
    }

    var provided = request.Headers["X-Bootstrap-Token"].ToString();
    if (string.IsNullOrWhiteSpace(provided))
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            provided = header["Bearer ".Length..].Trim();
        }
    }

    return !string.IsNullOrWhiteSpace(provided) &&
        FixedTimeEquals(provided, bootstrapToken);
}

static byte[] SignAdminToken(IConfiguration configuration, string expiresUnix, string encodedUsername)
{
    var secret = GetAdminTokenSecret(configuration);
    if (string.IsNullOrWhiteSpace(secret))
    {
        return [];
    }

    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    return hmac.ComputeHash(Encoding.UTF8.GetBytes($"ofrinio-admin:{expiresUnix}:{encodedUsername}"));
}

static string Base64UrlEncode(byte[] bytes)
{
    return Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

static byte[] Base64UrlDecode(string value)
{
    var base64 = value
        .Replace('-', '+')
        .Replace('_', '/');
    base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
    return Convert.FromBase64String(base64);
}

static AdminAvailabilityDto ReadAdminAvailability(SqlDataReader reader)
{
    return new AdminAvailabilityDto(
        reader.GetDateTime(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        NormalizeStatus(reader.GetString(1)),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetDateTime(5));
}

static async Task UpsertAdminUser(
    SqlConnection connection,
    string username,
    string passwordHash,
    string? displayName)
{
    const string sql = """
        merge dbo.AdminUsers as target
        using (values (@Username, @PasswordHash, @DisplayName)) as source
            ([Username], [PasswordHash], [DisplayName])
        on target.[Username] = source.[Username]
        when matched then
            update set
                [PasswordHash] = source.[PasswordHash],
                [DisplayName] = source.[DisplayName],
                [IsActive] = 1,
                [UpdatedAt] = sysutcdatetime()
        when not matched then
            insert ([Username], [PasswordHash], [DisplayName], [IsActive], [CreatedAt], [UpdatedAt])
            values (source.[Username], source.[PasswordHash], source.[DisplayName], 1, sysutcdatetime(), sysutcdatetime());
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@Username", username);
    command.Parameters.AddWithValue("@PasswordHash", passwordHash);
    command.Parameters.AddWithValue("@DisplayName", string.IsNullOrWhiteSpace(displayName) ? DBNull.Value : displayName.Trim());
    await command.ExecuteNonQueryAsync();
}

static async Task DeleteAvailabilityRange(
    SqlConnection connection,
    System.Data.Common.DbTransaction transaction,
    DateOnly startDate,
    DateOnly endDate)
{
    const string sql = """
        delete from dbo.Availability
        where [Date] between @StartDate and @EndDate;
        """;

    await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction);
    command.Parameters.AddWithValue("@StartDate", startDate.ToDateTime(TimeOnly.MinValue));
    command.Parameters.AddWithValue("@EndDate", endDate.ToDateTime(TimeOnly.MinValue));
    await command.ExecuteNonQueryAsync();
}

static async Task UpsertAvailability(
    SqlConnection connection,
    System.Data.Common.DbTransaction transaction,
    DateOnly date,
    string status,
    string? guestName,
    string? phone,
    string? notes,
    string source)
{
    const string sql = """
        merge dbo.Availability as target
        using (values (@Date, @Status, @GuestName, @Phone, @Notes, @Source)) as source
            ([Date], [Status], [GuestName], [Phone], [Notes], [Source])
        on target.[Date] = source.[Date]
        when matched then
            update set
                [Status] = source.[Status],
                [GuestName] = source.[GuestName],
                [Phone] = source.[Phone],
                [Notes] = source.[Notes],
                [Source] = source.[Source],
                [UpdatedAt] = sysutcdatetime()
        when not matched then
            insert ([Date], [Status], [GuestName], [Phone], [Notes], [Source], [UpdatedAt])
            values (source.[Date], source.[Status], source.[GuestName], source.[Phone], source.[Notes], source.[Source], sysutcdatetime());
        """;

    await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction);
    command.Parameters.AddWithValue("@Date", date.ToDateTime(TimeOnly.MinValue));
    command.Parameters.AddWithValue("@Status", status);
    command.Parameters.AddWithValue("@GuestName", string.IsNullOrWhiteSpace(guestName) ? DBNull.Value : guestName.Trim());
    command.Parameters.AddWithValue("@Phone", string.IsNullOrWhiteSpace(phone) ? DBNull.Value : phone.Trim());
    command.Parameters.AddWithValue("@Notes", string.IsNullOrWhiteSpace(notes) ? DBNull.Value : notes.Trim());
    command.Parameters.AddWithValue("@Source", source);
    await command.ExecuteNonQueryAsync();
}

static async Task<List<ImportedAvailabilityEntry>> LoadSupabaseAvailability(
    IConfiguration configuration,
    HttpClient httpClient)
{
    var supabaseUrl = GetSupabaseUrl(configuration)?.TrimEnd('/');
    var serviceRoleKey = GetSupabaseServiceRoleKey(configuration);

    if (!string.IsNullOrWhiteSpace(serviceRoleKey))
    {
        var rows = await FetchSupabaseRows<SupabaseReservationRow>(
            httpClient,
            $"{supabaseUrl}/rest/v1/reservations?select=date,status,name,phone,notes&order=date.asc",
            serviceRoleKey);

        return MergeImportedAvailability(rows.Select(row => new ImportedAvailabilityEntry(
            ParseImportedDate(row.Date),
            NormalizeStatus(row.Status),
            row.Name,
            row.Phone,
            row.Notes,
            "supabase-reservations")));
    }

    var anonKey = GetSupabaseAnonKey(configuration);
    if (string.IsNullOrWhiteSpace(anonKey))
    {
        return [];
    }

    var publicRows = await FetchSupabaseRows<SupabasePublicAvailabilityRow>(
        httpClient,
        $"{supabaseUrl}/rest/v1/public_availability?select=date,status&order=date.asc",
        anonKey);

    return MergeImportedAvailability(publicRows.Select(row => new ImportedAvailabilityEntry(
        ParseImportedDate(row.Date),
        NormalizeStatus(row.Status),
        null,
        null,
        null,
        "supabase-public-availability")));
}

static async Task<List<T>> FetchSupabaseRows<T>(HttpClient httpClient, string url, string key)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.Add("apikey", key);
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

    using var response = await httpClient.SendAsync(request);
    response.EnsureSuccessStatusCode();

    return await response.Content.ReadFromJsonAsync<List<T>>() ?? [];
}

static DateOnly ParseImportedDate(string value)
{
    return DateOnly.ParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
}

static List<ImportedAvailabilityEntry> MergeImportedAvailability(IEnumerable<ImportedAvailabilityEntry> entries)
{
    var statusWeight = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["free"] = 0,
        ["pending"] = 1,
        ["booked"] = 2
    };
    var byDate = new Dictionary<DateOnly, ImportedAvailabilityEntry>();

    foreach (var entry in entries)
    {
        if (!byDate.TryGetValue(entry.Date, out var current) ||
            statusWeight[entry.Status] >= statusWeight[current.Status])
        {
            byDate[entry.Date] = entry;
        }
    }

    return byDate
        .Values
        .Where(entry => entry.Status != "free")
        .OrderBy(entry => entry.Date)
        .ToList();
}

static AvailabilityDto[] FallbackAvailability()
{
    return
    [
        new("2026-06-06", "booked"),
        new("2026-06-07", "booked"),
        new("2026-06-08", "booked"),
        new("2026-06-09", "booked"),
        new("2026-06-10", "booked"),
        new("2026-06-11", "booked"),
        new("2026-06-12", "booked"),
        new("2026-06-16", "booked"),
        new("2026-06-17", "booked"),
        new("2026-06-18", "booked"),
        new("2026-06-19", "booked"),
        new("2026-06-21", "booked"),
        new("2026-06-22", "booked"),
        new("2026-06-23", "booked"),
        new("2026-06-24", "booked"),
        new("2026-06-25", "booked"),
        new("2026-06-26", "booked"),
        new("2026-06-27", "booked"),
        new("2026-06-28", "booked"),
        new("2026-06-29", "booked"),
        new("2026-07-01", "booked"),
        new("2026-07-02", "booked"),
        new("2026-07-03", "booked"),
        new("2026-07-04", "booked"),
        new("2026-07-05", "booked"),
        new("2026-07-06", "booked"),
        new("2026-07-07", "booked"),
        new("2026-07-08", "booked"),
        new("2026-07-09", "booked"),
        new("2026-07-10", "booked"),
        new("2026-07-11", "booked"),
        new("2026-07-13", "pending"),
        new("2026-07-14", "pending"),
        new("2026-07-15", "pending"),
        new("2026-07-16", "pending"),
        new("2026-07-17", "pending"),
        new("2026-07-18", "pending"),
        new("2026-07-19", "pending"),
        new("2026-07-20", "booked"),
        new("2026-07-21", "booked"),
        new("2026-07-22", "booked"),
        new("2026-07-23", "booked"),
        new("2026-07-24", "booked"),
        new("2026-07-25", "booked"),
        new("2026-07-26", "booked"),
        new("2026-07-27", "booked")
    ];
}

record AvailabilityDto(string Date, string Status);

record AdminAvailabilityDto(
    string Date,
    string Status,
    string? GuestName,
    string? Phone,
    string? Notes,
    DateTime? UpdatedAt);

record AdminLoginRequest(string Username, string Password);

record AdminLoginResponse(string Token, DateTimeOffset ExpiresAt);

record AdminMeResponse(string Username);

record AdminBootstrapUsersRequest(List<AdminBootstrapUserDto> Users);

record AdminBootstrapUserDto(string Username, string Password, string? DisplayName);

record AdminSettingsRequest(bool SupabaseSyncEnabled);

record AdminSettingsResponse(bool SupabaseSyncEnabled);

record AdminAvailabilityRangeRequest(
    string StartDate,
    string EndDate,
    string Status,
    string? GuestName,
    string? Phone,
    string? Notes);

record SupabaseImportRequest(bool ReplaceExisting = false);

record BookingRequestDto(
    string Name,
    string Phone,
    string RequestedDates,
    string? Message);

record SupabasePublicAvailabilityRow(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("status")] string? Status);

record SupabaseReservationRow(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("notes")] string? Notes);

record ImportedAvailabilityEntry(
    DateOnly Date,
    string Status,
    string? GuestName,
    string? Phone,
    string? Notes,
    string Source);
