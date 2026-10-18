using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using IrhamTrade.Server.Data;
using IrhamTrade.Server.Services;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

try
{
    var keysPath = Path.Combine(Path.GetTempPath(), "irhamtrade-keys");
    Directory.CreateDirectory(keysPath);
    builder.Services.AddDataProtection()
       .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
       .SetApplicationName("IrhamTrade");
}
catch
{
    builder.Services.AddDataProtection().SetApplicationName("IrhamTrade");
}

var postgresUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
               ?? Environment.GetEnvironmentVariable("DATABASE_INTERNAL_URL");

var defaultSqlite = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=/tmp/irhamtrade.db";
var connStr = defaultSqlite;

if (!string.IsNullOrWhiteSpace(postgresUrl))
{
    try
    {
        var url = postgresUrl.Trim();
        if (url.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
            url = "postgresql://" + url.Substring("postgres://".Length);

        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "";
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        var database = uri.AbsolutePath.Trim('/').Split('?')[0];
        var host = uri.Host;
        var portNum = uri.Port > 0 ? uri.Port : 5432;

        connStr = $"Host={host};Port={portNum};Database={database};Username={username};Password={password};SSL Mode=Require;Trust Server Certificate=true;Pooling=true;Minimum Pool Size=1;Maximum Pool Size=20;Timeout=15;Command Timeout=30;";
    }
    catch
    {
        connStr = defaultSqlite;
    }
}

if (connStr.Contains("Host=", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        options.UseNpgsql(connStr, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
            npgsqlOptions.CommandTimeout(30);
        });
        options.EnableSensitiveDataLogging(false);
    });
}
else
{
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        options.UseSqlite(connStr);
    });
}

builder.Services.AddSingleton<FirebaseService>();
builder.Services.AddScoped<FirestoreService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

var jwtKey = builder.Configuration["Jwt:Key"]
   ?? builder.Configuration["Jwt__Key"]
   ?? Environment.GetEnvironmentVariable("Jwt__Key")
   ?? Environment.GetEnvironmentVariable("JWT_KEY")
   ?? "IrhamTrade-Dev-Key-Only-Not-For-Prod-32-Chars-Min-2026!";

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "IrhamTrade.Server";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "IrhamTrade.App";

if (jwtKey.Length < 32)
{
    jwtKey = "IrhamTrade-Dev-Key-Only-Not-For-Prod-32-Chars-Min-2026!";
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false;
    options.SaveToken = true;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromMinutes(2)
    };
});

builder.Services.AddAuthorization();
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = null;
    o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseForwardedHeaders();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/Auth"))
    {
        await next();
        return;
    }
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Unhandled exception for {Path}", context.Request.Path);
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Server is busy at the moment. Please try again in a few seconds" });
        }
    }
});

app.MapGet("/", () => Results.Ok(new { status = "IRHAM TRADE Server is LIVE on .NET 8", project = "irhamtrade-31ca6", framework = ".NET 8", time = DateTime.UtcNow }));
app.MapHealthChecks("/health");
app.MapGet("/health/detailed", async (AppDbContext db, FirebaseService fb, ILogger<Program> logger) =>
{
    var dbOk = false;
    try { dbOk = await db.Database.CanConnectAsync(); } catch (Exception ex) { logger.LogWarning(ex, "DB health check failed"); }
    return Results.Ok(new
    {
        status = "ok",
        framework = ".NET 8",
        database = dbOk ? "connected" : "connecting",
        firebase = fb.IsInitialized ? "connected" : "sqlite-only-fallback",
        project = fb.ProjectId,
        time = DateTime.UtcNow
    });
});

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    for (int attempt = 1; attempt <= 3; attempt++)
    {
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            try
            {
                if (db.Database.IsNpgsql())
                {
                    await db.Database.MigrateAsync();
                    logger.LogInformation("Postgres migrated successfully on .NET 8");
                }
                else
                {
                    await db.Database.EnsureCreatedAsync();
                    logger.LogInformation("Sqlite ensured created on .NET 8");
                }
            }
            catch (Exception migrateEx)
            {
                logger.LogWarning(migrateEx, "Migrate failed, trying EnsureCreated fallback attempt {Attempt}", attempt);
                if (attempt == 1)
                {
                    try { await db.Database.EnsureCreatedAsync(); logger.LogInformation("Database fallback EnsureCreated succeeded"); break; } catch { }
                }
                throw;
            }
            break;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Database init attempt {Attempt} failed", attempt);
            if (attempt == 3)
            {
                logger.LogError(ex, "Database init failed after 3 attempts - server will continue and retry on request");
            }
            else
            {
                await Task.Delay(1500 * attempt);
            }
        }
    }
}

app.Logger.LogInformation("Server running on port {Port} Project irhamtrade-31ca6", port);
app.Run();