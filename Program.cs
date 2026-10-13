using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using IrhamTrade.Server.Data;
using IrhamTrade.Server.Services;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://+:{port}");

// DATABASE Boss - use /tmp for Render write permission Boss
var connStr = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=/tmp/irhamtrade.db";
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connStr));

// FIREBASE Boss
builder.Services.AddSingleton<FirebaseService>();
builder.Services.AddScoped<AuthService>();

// JWT Boss - SAFE Boss - will not crash if null Boss
var jwtKey = builder.Configuration["Jwt:Key"]
          ?? builder.Configuration["Jwt__Key"]
          ?? Environment.GetEnvironmentVariable("Jwt__Key")
          ?? "IrhamTrade_Super_Secret_Key_2025_For_JWT_32_Chars_Min_Boss_123456";

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "IrhamTrade";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "IrhamTradeUsers";

// DEBUG Boss - see in logs Boss
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

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/", () => "IrhamTrade Server LIVE Boss!");

Console.WriteLine($"Boss - Server running on port {port} Boss!");

app.Run();