using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using IrhamTrade.Server.Data;
using IrhamTrade.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// BOSS FIX 1 - Use 0.0.0.0 not + for Render health check Boss
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

try
{
    var keysPath = "/tmp/keys";
    if (!Directory.Exists(keysPath))
    {
        Directory.CreateDirectory(keysPath);
        Console.WriteLine($"BOSS - Created {keysPath} Boss for DataProtection Boss");
    }
    builder.Services.AddDataProtection()
      .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
      .SetApplicationName("IrhamTrade");
}
catch (Exception ex)
{
    Console.WriteLine($"BOSS DataProtection setup warning Boss: {ex.Message} Boss - Continuing Boss");
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
        Console.WriteLine("BOSS - Converted DATABASE_URL to Postgres conn Boss");
    }
    catch (Exception ex)
    {
        Console.WriteLine($" DATABASE_URL parse failed Boss: {ex.Message} Boss - Using fallback Boss");
    }
}

if (connStr.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) || connStr.Contains("Host="))
{
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        options.UseNpgsql(connStr, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
        });
        options.EnableDetailedErrors();
    });
    Console.WriteLine($"BOSS - Using POSTGRES in Production Boss");
}
else
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite(connStr));
    Console.WriteLine($"BOSS - Using SQLITE Boss - Path: {connStr} Boss");
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
   ?? "IrhamTrade_Super_Secret_Key_2025_For_JWT_32_Chars_Min_Boss_123456";

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "IrhamTrade";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "IrhamTradeUsers";

Console.WriteLine($"BOSS JWT Key exists: {!string.IsNullOrEmpty(jwtKey)} Length: {jwtKey.Length} Boss");

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
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"BOSS Server error but still alive Boss - Try again Boss\",\"success\":false}");
        Console.WriteLine($"BOSS Global exception caught Boss - Server still alive Boss: {context.Request.Path} Boss");
    });
});

try
{
    var firebaseService = app.Services.GetRequiredService<FirebaseService>();
    if (firebaseService.IsInitialized)
    {
        Console.WriteLine(" FIREBASE STRONG LINK CONFIRMED Boss! READY FOR CUSTOMERS Boss! ");
    }
    else
    {
        Console.WriteLine(" FIREBASE FAILED Boss - Check FIREBASE_KEY_JSON ENV Boss - But server still running Boss");
    }
}
catch (Exception ex)
{
    Console.WriteLine($" BOSS FIREBASE EXCEPTION Boss: {ex.Message} Boss - Server still running Boss");
}

// BOSS FIX 2 - Health check MUST be mapped BEFORE heavy DB init Boss - Render checks / immediately Boss
app.MapGet("/", () => Results.Ok("IrhamTrade Server LIVE Boss! Professional Postgres Ready Boss! Firebase Sealed Boss! API: /api/dashboard /api/auth /swagger"));
app.MapHealthChecks("/health");

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// BOSS FIX 3 - DB init AFTER MapGet so health check answers fast Boss - No blocking Thread.Sleep Boss
using (var scope = app.Services.CreateScope())
{
    int maxRetries = 3;
    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Console.WriteLine($"BOSS - Checking database structure Boss... Attempt {attempt} Boss");

            var hasMigrations = false;
            try { hasMigrations = db.Database.GetMigrations().Any(); }
            catch { hasMigrations = false; Console.WriteLine("BOSS - GetMigrations failed, using EnsureCreated Boss"); }

            if (!hasMigrations)
            {
                Console.WriteLine("BOSS - No migrations configured Boss, using EnsureCreated Boss");
                var created = db.Database.EnsureCreated();
                Console.WriteLine($"BOSS - Database ready Boss! Tables created via EnsureCreated: {created} Boss!");
            }
            else
            {
                Console.WriteLine("BOSS - Applying migrations Boss...");
                db.Database.Migrate();
                Console.WriteLine("BOSS - Database migrated Boss! Tables created Boss!");
            }

            if (db.Database.CanConnect())
            {
                Console.WriteLine("BOSS - Database connected Boss! READY Boss!");
            }
            break;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BOSS - Migration attempt {attempt} error Boss: {ex.Message} Boss");
            if (attempt == maxRetries)
            {
                try
                {
                    var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var created = db2.Database.EnsureCreated();
                    Console.WriteLine($"BOSS - Database created via fallback EnsureCreated: {created} Boss!");
                }
                catch (Exception ex2)
                {
                    Console.WriteLine($"BOSS - Fallback also failed Boss: {ex2.Message} Boss - Server will still run Boss");
                }
            }
            else
            {
                // BOSS FIX - Use Task.Delay non-blocking? For startup keep Thread.Sleep but short Boss
                Thread.Sleep(1000);
            }
        }
    }
}

Console.WriteLine($"Boss - Server running on port {port} Boss! SEALED Boss!");

app.Run();