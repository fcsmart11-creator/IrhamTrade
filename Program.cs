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

var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://+:{port}");

// ===== BOSS PROFESSIONAL DataProtection Boss =====
builder.Services.AddDataProtection()
   .PersistKeysToFileSystem(new DirectoryInfo("/tmp/keys"))
   .SetApplicationName("IrhamTrade");

// ===== BOSS PROFESSIONAL DATABASE Boss - FIXED Boss =====
var postgresUrl = Environment.GetEnvironmentVariable("DATABASE_URL") ?? Environment.GetEnvironmentVariable("DATABASE_INTERNAL_URL");
var connStr = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=/tmp/irhamtrade.db";

// If Render gives DATABASE_URL like postgres://... convert to Npgsql format Boss
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
        Console.WriteLine($" DATABASE_URL parse failed Boss: {ex.Message} Boss");
    }
}

if (connStr.StartsWith("postgres", StringComparison.OrdinalIgnoreCase) || connStr.Contains("Host="))
{
    // BOSS LIVE - Postgres on Render - Reliable Boss
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        options.UseNpgsql(connStr, npgsqlOptions =>
        {
            npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
        });
        options.EnableDetailedErrors();
    });
    Console.WriteLine($"BOSS - Using POSTGRES in Production Boss Boss - STRONG Boss");
}
else
{
    // BOSS LOCAL - Sqlite for testing Boss
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite(connStr));
    Console.WriteLine($"BOSS - Using SQLITE for Local/Test Boss - NOT FOR PRODUCTION Boss");
}

// FIREBASE Boss - STRONG LINK Boss
builder.Services.AddSingleton<FirebaseService>();
builder.Services.AddScoped<AuthService>();

// JWT Boss
var jwtKey = builder.Configuration["Jwt:Key"]
         ?? builder.Configuration["Jwt__Key"]
         ?? Environment.GetEnvironmentVariable("Jwt__Key")
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

// ===== BOSS FORCE FIREBASE INIT Boss - STRONG LINK MUST SHOW IN LOGS Boss =====
try
{
    var firebaseService = app.Services.GetRequiredService<FirebaseService>();
    if (firebaseService.IsInitialized)
    {
        Console.WriteLine(" FIREBASE STRONG LINK CONFIRMED Boss! READY FOR CUSTOMERS Boss! ");
    }
    else
    {
        Console.WriteLine(" FIREBASE FAILED Boss - Check FIREBASE_KEY_JSON ENV Boss");
    }
}
catch (Exception ex)
{
    Console.WriteLine($" BOSS FIREBASE EXCEPTION Boss: {ex.Message} Boss");
}

// ===== BOSS PROFESSIONAL AUTO-MIGRATION Boss =====
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Console.WriteLine("BOSS - Applying migrations Boss...");
        db.Database.Migrate();
        Console.WriteLine("BOSS - Database ready Boss! Tables created Boss!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"BOSS - Migration error Boss (will retry next start): {ex.Message} Boss");
    }
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/", () => "IrhamTrade Server LIVE Boss! Professional Postgres Ready Boss!");
app.MapHealthChecks("/health");

Console.WriteLine($"Boss - Server running on port {port} Boss!");

app.Run();