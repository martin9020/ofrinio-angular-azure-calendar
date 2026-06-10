using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;

namespace Ofrinio.Functions;

public class ApiFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient HttpClient = new();

    [Function("Health")]
    public static Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request)
    {
        return Json(request, new
        {
            ok = true,
            app = "ofrinio-api",
            databaseConfigured = !string.IsNullOrWhiteSpace(GetConnectionString()),
            adminConfigured = IsAdminConfigured(),
            supabaseImportConfigured = IsSupabaseImportConfigured()
        });
    }

    [Function("Availability")]
    public static async Task<HttpResponseData> Availability(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "availability")] HttpRequestData request)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return await Json(request, FallbackAvailability());
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

        return await Json(request, rows);
    }

    [Function("BookingRequests")]
    public static async Task<HttpResponseData> BookingRequests(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "booking-requests")] HttpRequestData httpRequest)
    {
        var request = await JsonSerializer.DeserializeAsync<BookingRequestDto>(
            httpRequest.Body,
            JsonOptions);

        if (request is null ||
            string.IsNullOrWhiteSpace(request.Name) ||
            string.IsNullOrWhiteSpace(request.Phone) ||
            string.IsNullOrWhiteSpace(request.RequestedDates))
        {
            return await Json(
                httpRequest,
                new { error = "Name, phone and requestedDates are required." },
                HttpStatusCode.BadRequest);
        }

        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return await Json(
                httpRequest,
                new
                {
                    saved = false,
                    message = "Database is not configured. Request accepted in demo mode."
                },
                HttpStatusCode.Accepted);
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

        return await Json(httpRequest, new { saved = true }, HttpStatusCode.Created);
    }

    [Function("AdminLogin")]
    public static async Task<HttpResponseData> AdminLogin(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "owner/login")] HttpRequestData httpRequest)
    {
        var request = await JsonSerializer.DeserializeAsync<AdminLoginRequest>(
            httpRequest.Body,
            JsonOptions);

        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString) ||
            string.IsNullOrWhiteSpace(GetAdminTokenSecret()))
        {
            return await Json(
                httpRequest,
                new { error = "Set AZURE_SQL_CONNECTION_STRING and OFRINIO_ADMIN_TOKEN_SECRET in the Azure Function app settings." },
                HttpStatusCode.ServiceUnavailable);
        }

        if (request is null ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            !await VerifyAdminUser(connectionString, request.Username, request.Password))
        {
            return httpRequest.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var expiresAt = DateTimeOffset.UtcNow.AddHours(12);
        return await Json(httpRequest, new AdminLoginResponse(
            CreateAdminToken(expiresAt, NormalizeUsername(request.Username)),
            expiresAt));
    }

    [Function("AdminBootstrapUsers")]
    public static async Task<HttpResponseData> AdminBootstrapUsers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "owner/bootstrap-users")] HttpRequestData httpRequest)
    {
        if (!IsAuthorizedBootstrap(httpRequest))
        {
            return httpRequest.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var request = await JsonSerializer.DeserializeAsync<AdminBootstrapUsersRequest>(
            httpRequest.Body,
            JsonOptions);

        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return await Json(httpRequest, new { error = "Azure SQL is not configured." }, HttpStatusCode.ServiceUnavailable);
        }

        if (request is null || request.Users.Count == 0)
        {
            return await Json(httpRequest, new { error = "At least one user is required." }, HttpStatusCode.BadRequest);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await EnsureDatabaseSchema(connection);

        foreach (var user in request.Users)
        {
            if (string.IsNullOrWhiteSpace(user.Username) ||
                string.IsNullOrWhiteSpace(user.Password))
            {
                return await Json(httpRequest, new { error = "Each user needs username and password." }, HttpStatusCode.BadRequest);
            }

            await UpsertAdminUser(
                connection,
                NormalizeUsername(user.Username),
                HashPassword(user.Password),
                user.DisplayName);
        }

        return await Json(httpRequest, new { saved = request.Users.Count });
    }

    [Function("AdminAvailability")]
    public static async Task<HttpResponseData> AdminAvailability(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "owner/availability")] HttpRequestData httpRequest)
    {
        if (!IsAuthorizedAdmin(httpRequest))
        {
            return httpRequest.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return await Json(httpRequest, new { error = "Azure SQL is not configured." }, HttpStatusCode.ServiceUnavailable);
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

        return await Json(httpRequest, rows);
    }

    [Function("AdminSettings")]
    public static async Task<HttpResponseData> AdminSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "owner/settings")] HttpRequestData httpRequest)
    {
        if (!IsAuthorizedAdmin(httpRequest))
        {
            return httpRequest.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return await Json(httpRequest, new { error = "Azure SQL is not configured." }, HttpStatusCode.ServiceUnavailable);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await EnsureDatabaseSchema(connection);

        return await Json(httpRequest, new AdminSettingsResponse(
            await GetSupabaseSyncEnabled(connection)));
    }

    [Function("AdminSaveSettings")]
    public static async Task<HttpResponseData> AdminSaveSettings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "owner/settings")] HttpRequestData httpRequest)
    {
        if (!IsAuthorizedAdmin(httpRequest))
        {
            return httpRequest.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var request = await JsonSerializer.DeserializeAsync<AdminSettingsRequest>(
            httpRequest.Body,
            JsonOptions);

        if (request is null)
        {
            return await Json(httpRequest, new { error = "Settings payload is required." }, HttpStatusCode.BadRequest);
        }

        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return await Json(httpRequest, new { error = "Azure SQL is not configured." }, HttpStatusCode.ServiceUnavailable);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await EnsureDatabaseSchema(connection);
        await SetAppSetting(connection, "SupabaseSyncEnabled", request.SupabaseSyncEnabled ? "true" : "false");

        return await Json(httpRequest, new AdminSettingsResponse(request.SupabaseSyncEnabled));
    }

    [Function("AdminMe")]
    public static Task<HttpResponseData> AdminMe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "owner/me")] HttpRequestData httpRequest)
    {
        var identity = GetAuthorizedAdminIdentity(httpRequest);
        return identity is null
            ? Task.FromResult(httpRequest.CreateResponse(HttpStatusCode.Unauthorized))
            : Json(httpRequest, new AdminMeResponse(identity));
    }

    [Function("AdminAvailabilityRange")]
    public static async Task<HttpResponseData> AdminAvailabilityRange(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "owner/availability/range")] HttpRequestData httpRequest)
    {
        if (!IsAuthorizedAdmin(httpRequest))
        {
            return httpRequest.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var request = await JsonSerializer.DeserializeAsync<AdminAvailabilityRangeRequest>(
            httpRequest.Body,
            JsonOptions);

        if (request is null ||
            !TryParseDate(request.StartDate, out var startDate) ||
            !TryParseDate(request.EndDate, out var endDate))
        {
            return await Json(httpRequest, new { error = "startDate and endDate must use yyyy-MM-dd." }, HttpStatusCode.BadRequest);
        }

        if (endDate < startDate)
        {
            return await Json(httpRequest, new { error = "endDate must be on or after startDate." }, HttpStatusCode.BadRequest);
        }

        var status = NormalizeStatus(request.Status);
        var dayCount = endDate.DayNumber - startDate.DayNumber + 1;
        if (dayCount > 370)
        {
            return await Json(httpRequest, new { error = "Date ranges longer than 370 days are not allowed." }, HttpStatusCode.BadRequest);
        }

        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return await Json(httpRequest, new { error = "Azure SQL is not configured." }, HttpStatusCode.ServiceUnavailable);
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
        return await Json(httpRequest, new { saved = true, days = dayCount, status });
    }

    [Function("AdminImportSupabase")]
    public static async Task<HttpResponseData> AdminImportSupabase(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "owner/import-supabase")] HttpRequestData httpRequest)
    {
        if (!IsAuthorizedAdmin(httpRequest))
        {
            return httpRequest.CreateResponse(HttpStatusCode.Unauthorized);
        }

        var request = await JsonSerializer.DeserializeAsync<SupabaseImportRequest>(
            httpRequest.Body,
            JsonOptions);

        try
        {
            var result = await SyncSupabaseToAzure(request?.ReplaceExisting == true);
            return await Json(httpRequest, new
            {
                imported = result.Imported,
                source = result.Source,
                replacedExisting = request?.ReplaceExisting == true
            });
        }
        catch (InvalidOperationException error)
        {
            return await Json(httpRequest, new { error = error.Message }, HttpStatusCode.ServiceUnavailable);
        }
    }

    [Function("SyncSupabaseAvailability")]
    public static async Task SyncSupabaseAvailability(
        [TimerTrigger("0 */15 * * * *")] TimerInfo timerInfo)
    {
        if (!await IsSupabaseSyncEnabled())
        {
            return;
        }

        await SyncSupabaseToAzure(replaceExisting: false);
    }

    private static string? GetConnectionString()
    {
        return Environment.GetEnvironmentVariable("AZURE_SQL_CONNECTION_STRING");
    }

    private static string? GetBootstrapToken()
    {
        return Environment.GetEnvironmentVariable("OFRINIO_ADMIN_BOOTSTRAP_TOKEN");
    }

    private static string? GetAdminTokenSecret()
    {
        return Environment.GetEnvironmentVariable("OFRINIO_ADMIN_TOKEN_SECRET");
    }

    private static bool IsAdminConfigured()
    {
        return GetAllowedAdminEmails().Count > 0 ||
            (!string.IsNullOrWhiteSpace(GetConnectionString()) &&
             !string.IsNullOrWhiteSpace(GetAdminTokenSecret()));
    }

    private static IReadOnlySet<string> GetAllowedAdminEmails()
    {
        return (Environment.GetEnvironmentVariable("OFRINIO_ADMIN_EMAILS") ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSupabaseImportConfigured()
    {
        return !string.IsNullOrWhiteSpace(GetSupabaseUrl()) &&
            (!string.IsNullOrWhiteSpace(GetSupabaseAnonKey()) ||
             !string.IsNullOrWhiteSpace(GetSupabaseServiceRoleKey()));
    }

    private static async Task<bool> IsSupabaseSyncEnabled()
    {
        var connectionString = GetConnectionString();
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await EnsureDatabaseSchema(connection);

            return await GetSupabaseSyncEnabled(connection);
        }

        return string.Equals(
            Environment.GetEnvironmentVariable("SUPABASE_SYNC_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetSupabaseUrl()
    {
        return Environment.GetEnvironmentVariable("SUPABASE_URL")
            ?? "https://hqmgnouwuastlsenalre.supabase.co";
    }

    private static string? GetSupabaseAnonKey()
    {
        return Environment.GetEnvironmentVariable("SUPABASE_ANON_KEY")
            ?? "sb_publishable_TkdiVOTUPQqrkuY3UVPC1A_BYWj7KnC";
    }

    private static string? GetSupabaseServiceRoleKey()
    {
        return Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
    }

    private static string NormalizeStatus(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "booked" or "reserved" or "confirmed" or "потвърдена" => "booked",
            "pending" or "request" or "чакаща" => "pending",
            _ => "free"
        };
    }

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        return DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static bool FixedTimeEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static string NormalizeUsername(string username)
    {
        return username.Trim().ToLowerInvariant();
    }

    private static string HashPassword(string password)
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

    private static bool VerifyPassword(string password, string encodedHash)
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

    private static string CreateAdminToken(DateTimeOffset expiresAt, string username)
    {
        var expiresUnix = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var encodedUsername = Base64UrlEncode(Encoding.UTF8.GetBytes(username));
        var signature = SignAdminToken(expiresUnix, encodedUsername);
        return $"{expiresUnix}.{encodedUsername}.{Base64UrlEncode(signature)}";
    }

    private static bool IsAuthorizedAdmin(HttpRequestData request)
    {
        return GetAuthorizedAdminIdentity(request) is not null;
    }

    private static string? GetAuthorizedAdminIdentity(HttpRequestData request)
    {
        return GetAuthorizedAdminEmail(request) ??
            GetAuthorizedAdminTokenUsername(request);
    }

    private static string? GetAuthorizedAdminEmail(HttpRequestData request)
    {
        var allowedEmails = GetAllowedAdminEmails();
        if (allowedEmails.Count == 0)
        {
            return null;
        }

        var email = HeaderValue(request, "X-MS-CLIENT-PRINCIPAL-NAME");
        if (string.IsNullOrWhiteSpace(email))
        {
            email = HeaderValue(request, "X-MS-CLIENT-PRINCIPAL-ID");
        }

        return !string.IsNullOrWhiteSpace(email) && allowedEmails.Contains(email)
            ? email
            : null;
    }

    private static string? GetAuthorizedAdminTokenUsername(HttpRequestData request)
    {
        if (string.IsNullOrWhiteSpace(GetAdminTokenSecret()))
        {
            return null;
        }

        if (!request.Headers.TryGetValues("Authorization", out var values))
        {
            return null;
        }

        var header = values.FirstOrDefault();
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

        var expectedSignature = Base64UrlEncode(SignAdminToken(parts[0], parts[1]));
        return FixedTimeEquals(parts[2], expectedSignature)
            ? Encoding.UTF8.GetString(Base64UrlDecode(parts[1]))
            : null;
    }

    private static bool IsAuthorizedBootstrap(HttpRequestData request)
    {
        var bootstrapToken = GetBootstrapToken();
        if (string.IsNullOrWhiteSpace(bootstrapToken))
        {
            return false;
        }

        var provided = HeaderValue(request, "X-Bootstrap-Token");
        if (string.IsNullOrWhiteSpace(provided))
        {
            var header = HeaderValue(request, "Authorization");
            if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                provided = header["Bearer ".Length..].Trim();
            }
        }

        return !string.IsNullOrWhiteSpace(provided) &&
            FixedTimeEquals(provided, bootstrapToken);
    }

    private static string HeaderValue(HttpRequestData request, string name)
    {
        return request.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault() ?? string.Empty
            : string.Empty;
    }

    private static byte[] SignAdminToken(string expiresUnix, string encodedUsername)
    {
        var secret = GetAdminTokenSecret();
        if (string.IsNullOrWhiteSpace(secret))
        {
            return [];
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes($"ofrinio-admin:{expiresUnix}:{encodedUsername}"));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value
            .Replace('-', '+')
            .Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
        return Convert.FromBase64String(base64);
    }

    private static async Task EnsureDatabaseSchema(SqlConnection connection)
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

    private static async Task<bool> GetSupabaseSyncEnabled(SqlConnection connection)
    {
        var saved = await GetAppSetting(connection, "SupabaseSyncEnabled");
        if (!string.IsNullOrWhiteSpace(saved))
        {
            return string.Equals(saved, "true", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(
            Environment.GetEnvironmentVariable("SUPABASE_SYNC_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> GetAppSetting(SqlConnection connection, string key)
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

    private static async Task SetAppSetting(SqlConnection connection, string key, string value)
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

    private static AdminAvailabilityDto ReadAdminAvailability(SqlDataReader reader)
    {
        return new AdminAvailabilityDto(
            reader.GetDateTime(0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            NormalizeStatus(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5));
    }

    private static async Task<bool> VerifyAdminUser(string connectionString, string username, string password)
    {
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
        command.Parameters.AddWithValue("@Username", NormalizeUsername(username));
        var passwordHash = await command.ExecuteScalarAsync() as string;

        return !string.IsNullOrWhiteSpace(passwordHash) &&
            VerifyPassword(password, passwordHash);
    }

    private static async Task UpsertAdminUser(
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

    private static async Task DeleteAvailabilityRange(
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

    private static async Task UpsertAvailability(
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

    private static async Task<List<ImportedAvailabilityEntry>> LoadSupabaseAvailability()
    {
        var supabaseUrl = GetSupabaseUrl()?.TrimEnd('/');
        var serviceRoleKey = GetSupabaseServiceRoleKey();

        if (!string.IsNullOrWhiteSpace(serviceRoleKey))
        {
            var rows = await FetchSupabaseRows<SupabaseReservationRow>(
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

        var anonKey = GetSupabaseAnonKey();
        if (string.IsNullOrWhiteSpace(anonKey))
        {
            return [];
        }

        var publicRows = await FetchSupabaseRows<SupabasePublicAvailabilityRow>(
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

    private static async Task<List<T>> FetchSupabaseRows<T>(string url, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("apikey", key);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await HttpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<T>>() ?? [];
    }

    private static DateOnly ParseImportedDate(string value)
    {
        return DateOnly.ParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static List<ImportedAvailabilityEntry> MergeImportedAvailability(IEnumerable<ImportedAvailabilityEntry> entries)
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

    private static async Task<SupabaseSyncResult> SyncSupabaseToAzure(bool replaceExisting)
    {
        var connectionString = GetConnectionString();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Azure SQL is not configured.");
        }

        var imported = await LoadSupabaseAvailability();
        if (imported.Count == 0)
        {
            throw new InvalidOperationException("No Supabase availability rows were found.");
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await EnsureDatabaseSchema(connection);
        await using var transaction = await connection.BeginTransactionAsync();

        if (replaceExisting)
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

        return new SupabaseSyncResult(
            saved,
            imported.Any(entry => entry.Source == "supabase-reservations")
                ? "supabase-reservations"
                : "supabase-public-availability");
    }

    private static AvailabilityDto[] FallbackAvailability()
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

    private static async Task<HttpResponseData> Json(
        HttpRequestData request,
        object value,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = request.CreateResponse(statusCode);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(value, JsonOptions));
        return response;
    }
}

public record AvailabilityDto(string Date, string Status);

public record AdminAvailabilityDto(
    string Date,
    string Status,
    string? GuestName,
    string? Phone,
    string? Notes,
    DateTime? UpdatedAt);

public record AdminLoginRequest(string Username, string Password);

public record AdminLoginResponse(string Token, DateTimeOffset ExpiresAt);

public record AdminMeResponse(string Username);

public record AdminBootstrapUsersRequest(List<AdminBootstrapUserDto> Users);

public record AdminBootstrapUserDto(string Username, string Password, string? DisplayName);

public record AdminSettingsRequest(bool SupabaseSyncEnabled);

public record AdminSettingsResponse(bool SupabaseSyncEnabled);

public record AdminAvailabilityRangeRequest(
    string StartDate,
    string EndDate,
    string Status,
    string? GuestName,
    string? Phone,
    string? Notes);

public record SupabaseImportRequest(bool ReplaceExisting = false);

public record BookingRequestDto(
    string Name,
    string Phone,
    string RequestedDates,
    string? Message);

public record SupabasePublicAvailabilityRow(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("status")] string? Status);

public record SupabaseReservationRow(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("phone")] string? Phone,
    [property: JsonPropertyName("notes")] string? Notes);

public record ImportedAvailabilityEntry(
    DateOnly Date,
    string Status,
    string? GuestName,
    string? Phone,
    string? Notes,
    string Source);

public record SupabaseSyncResult(int Imported, string Source);
