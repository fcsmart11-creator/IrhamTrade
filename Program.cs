using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using IrhamTrade.Server.Data;
using IrhamTrade.Server.Services;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

try
{
    var keysPath = "/tmp/keys";
    Directory.CreateDirectory(keysPath);
    builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("IrhamTrade");
}
catch
{
    builder.Services.AddDataProtection().SetApplicationName("IrhamTrade");
}

var postgresUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? Environment.GetEnvironmentVariable("DATABASE_INTERNAL_URL");
var connStr = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=/tmp/irhamtrade.db";

if (!string.IsNullOrEmpty(postgresUrl))
{
    try
    {
        var uri = new Uri(postgresUrl);
        var userInfo = uri.UserInfo.Split(':');
        connStr = $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.Trim('/')};Username={userInfo[0]};Password={userInfo[1]};SSL Mode=Require;Trust Server Certificate=true;";
    }
    catch { }
}

if (connStr.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) || connStr.Contains("Host="))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        options.UseNpgsql(connStr, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
        });
    });
}
else
{
    builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connStr));
}

builder.Services.AddSingleton<FirebaseService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<FirestoreService>();
builder.Services.AddHttpClient();

var jwtKey = builder.Configuration["Jwt:Key"]
 ?? builder.Configuration["Jwt__Key"]
 ?? Environment.GetEnvironmentVariable("Jwt__Key")
 ?? Environment.GetEnvironmentVariable("JWT_KEY")
 ?? "IrhamTrade-Dev-Key-Only-Not-For-Prod-32-Chars-Min-2026!";

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "IrhamTrade.Server";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "IrhamTrade.App";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };
});

builder.Services.AddAuthorization();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { success = false, message = "Server is busy at the moment. Please try again in a few seconds" });
    });
});

app.MapGet("/", () => Results.Ok(new { status = "IRHAM TRADE Server is LIVE on.NET 10", project = "irhamtrade-31ca6", framework = ".NET 10", time = DateTime.UtcNow }));
app.MapHealthChecks("/health");
app.MapGet("/health/detailed", (AppDbContext db, FirebaseService fb) =>
{
    var dbOk = false;
    try { dbOk = db.Database.CanConnect(); } catch { dbOk = false; }
    return Results.Ok(new
    {
        status = "ok",
        framework = ".NET 10",
        database = dbOk ? "connected" : "connecting",
        firebase = fb.IsInitialized ? "connected" : "connecting",
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
    for (int attempt = 1; attempt <= 3; attempt++)
    {
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

            var hasMigrations = false;
            try { hasMigrations = db.Database.GetMigrations().Any(); } catch { hasMigrations = false; }

            if (!hasMigrations)
            {
                var created = db.Database.EnsureCreated();
                logger.LogInformation("Database ensured created: {Created} on.NET 10", created);
            }
            else
            {
                db.Database.Migrate();
                logger.LogInformation("Database migrated successfully on.NET 10");
            }
            break;
        }
        catch (Exception ex)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogWarning(ex, "Database init attempt {Attempt} failed on.NET 10", attempt);
            if (attempt == 3)
            {
                try
                {
                    var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    db2.Database.EnsureCreated();
                }
                catch (Exception ex2)
                {
                    logger.LogError(ex2, "Database fallback failed - server will continue on.NET 10");
                }
            }
            else
            {
                Thread.Sleep(1000);
            }
        }
    }
}

app.Logger.LogInformation("Server running on port {Port} with.NET 10", port);
app.Run();