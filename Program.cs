using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using IrhamTrade.Server.Data;
using IrhamTrade.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ================= PORT & DATAPROTECTION =================
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

// ================= DATABASE - POSTGRES OR SQLITE =================
var postgresUrl = Environment.GetEnvironmentVariable("DATABASE_URL")
               ?? Environment.GetEnvironmentVariable("DATABASE_INTERNAL_URL");
var connStr = builder.Configuration.GetConnectionString("DefaultConnection")
           ?? "Data Source=/tmp/irhamtrade.db";

if (!string.IsNullOrEmpty(postgresUrl))
{
    try
    {
        var uri = new Uri(postgresUrl);
        var userInfo = uri.UserInfo.Split(':', 2);
        // Handle password with special chars
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";
        connStr = $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.Trim('/')};Username={userInfo[0]};Password={password};SSL Mode=Require;Trust Server Certificate=true;";
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to parse DATABASE_URL: {ex.Message}");
    }
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

// ================= SERVICES - LOGIN SECURED  =================
builder.Services.AddSingleton<FirebaseService>(); 
builder.Services.AddScoped<FirestoreService>();   
builder.Services.AddScoped<AuthService>();        
builder.Services.AddScoped<DashboardService>();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// ================= JWT - SECURED FOR LOGIN =================
var jwtKey = builder.Configuration["Jwt:Key"]
   ?? builder.Configuration["Jwt__Key"]
   ?? Environment.GetEnvironmentVariable("Jwt__Key")
   ?? Environment.GetEnvironmentVariable("JWT_KEY")
   ?? "IrhamTrade-Dev-Key-Only-Not-For-Prod-32-Chars-Min-2026!";

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "IrhamTrade.Server";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "IrhamTrade.App";

if (jwtKey.Length < 32)
{
    Console.WriteLine("JWT Key too short - using fallback secure key");
    jwtKey = "IrhamTrade-Dev-Key-Only-Not-For-Prod-32-Chars-Min-2026!";
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
.AddJwtBearer(options =>
{
    options.RequireHttpsMetadata = false; // Render is behind proxy
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
    options.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = context =>
        {
            Console.WriteLine($"JWT Auth failed: {context.Exception.Message}");
            return Task.CompletedTask;
        },
        OnChallenge = context =>
        {
            Console.WriteLine($"JWT Challenge: {context.ErrorDescription}");
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = null;
    o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});
builder.Services.AddHealthChecks();

var app = builder.Build();

// ================= MIDDLEWARE - ORDER MATTERS FOR LOGIN =================
// Don't use generic exception handler for /api/Auth - let AuthController return proper messages
app.Use(async (context, next) =>
{
    // Bypass generic error handler for Auth routes to return real error messages
    if (context.Request.Path.StartsWithSegments("/api/Auth"))
    {
        await next();
        return;
    }
    try { await next(); }
    catch (Exception ex)
    {
        Console.WriteLine($"Unhandled exception for {context.Request.Path}: {ex.Message}");
        if (!context.Response.HasStarted)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Server is busy at the moment. Please try again in a few seconds", detail = ex.Message });
        }
    }
});

app.MapGet("/", () => Results.Ok(new { status = "IRHAM TRADE Server is LIVE on .NET 8", project = "irhamtrade-31ca6", framework = ".NET 8", time = DateTime.UtcNow }));
app.MapHealthChecks("/health");
app.MapGet("/health/detailed", (AppDbContext db, FirebaseService fb) =>
{
    var dbOk = false;
    try { dbOk = db.Database.CanConnect(); } catch { dbOk = false; }
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
app.MapGet("/api/Auth/status", (FirebaseService fb) => Results.Ok(new { status = "Auth Ready", firebase = fb.IsInitialized ? "connected" : "sqlite-only", time = DateTime.UtcNow }));

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// ================= DATABASE INIT - LIKE FACEBOOK =================
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
                logger.LogInformation("Database ensured created: {Created} on .NET 8", created);
            }
            else
            {
                db.Database.Migrate();
                logger.LogInformation("Database migrated successfully on .NET 8");
            }
            break;
        }
        catch (Exception ex)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogWarning(ex, "Database init attempt {Attempt} failed", attempt);
            if (attempt == 3)
            {
                try
                {
                    var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    db2.Database.EnsureCreated();
                    logger.LogInformation("Database fallback EnsureCreated succeeded");
                }
                catch (Exception ex2)
                {
                    logger.LogError(ex2, "Database fallback failed - server will continue");
                }
            }
            else
            {
                Thread.Sleep(1500);
            }
        }
    }
}

app.Logger.LogInformation(" Server running on port {Port} Project irhamtrade-31ca6", port);
app.Run();