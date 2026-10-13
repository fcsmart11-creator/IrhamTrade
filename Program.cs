using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using IrhamTrade.Server.Data;
using IrhamTrade.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// FIX FOR RENDER REAL SERVER BOSS - PORT BOSS
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
builder.WebHost.UseUrls($"http://+:{port}");

// 1. DATABASE Boss - SQLite Boss - good for many users Boss
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. FIREBASE Boss - SINGLETON Boss - ONE connection for 10,000 users Boss - NO CRASH Boss
builder.Services.AddSingleton<FirebaseService>();

// 3. AUTH SERVICE Boss - uses FirebaseService Boss
builder.Services.AddScoped<AuthService>();

// 4. JWT AUTH Boss - using YOUR real key Boss
var jwtKey = builder.Configuration["Jwt:Key"];
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey!))
        };
    });

builder.Services.AddAuthorization();

// 5. CONTROLLERS + SWAGGER Boss
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 6. CORS Boss - for Android Boss
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// Pipeline Boss
app.UseSwagger();
app.UseSwaggerUI();

// FIX FOR RENDER BOSS - comment this Boss - causes crash on Render Boss
// app.UseHttpsRedirection();

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// HEALTH CHECK BOSS - FOR RENDER TO KNOW SERVER LIVE BOSS
app.MapGet("/", () => "IrhamTrade Server LIVE Boss!");

Console.WriteLine($"Boss - Server running on port {port} Boss! Project: {builder.Configuration["Firebase:ProjectId"]} Boss");

app.Run();