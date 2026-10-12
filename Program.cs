using FirebaseAdmin;
using Google.Apis.Auth.OAuth2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using IrhamTrade.Server.Data;
using IrhamTrade.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// 1. DATABASE Boss - SQLite Boss - good for many users Boss
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. FIREBASE Boss - SINGLETON Boss - ONE connection for 10,000 users Boss - NO CRASH Boss
// We create FirebaseService as Singleton Boss, it handles thread-safe init Boss
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
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

Console.WriteLine($"Boss - Server running Boss! Project: {builder.Configuration["Firebase:ProjectId"]} Boss");

app.Run();