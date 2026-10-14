using FirebaseAdmin.Auth;
using IrhamTrade.Server.Data;
using IrhamTrade.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace IrhamTrade.Server.Services
{
    public class AppUser
    {
        public string Id { get; set; } = "";
        public string Role { get; set; } = "";
        public string BusinessName { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Location { get; set; } = "";
        public string Email { get; set; } = "";
        public string Token { get; set; } = "";
    }

    public class AuthResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public AppUser? User { get; set; }
    }

    public class AuthService
    {
        private readonly AppDbContext _context;
        private readonly FirebaseService _firebaseService;
        private readonly IConfiguration _config;
        private readonly ILogger<AuthService> _logger;

        public AuthService(AppDbContext context, FirebaseService firebaseService, IConfiguration config, ILogger<AuthService> logger)
        {
            _context = context;
            _firebaseService = firebaseService;
            _config = config;
            _logger = logger;
        }

        // ===== FACEBOOK RETRY HELPER - 70 SEC TIMEOUT FOR RENDER COLD START =====
        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, T fallback, string opName, int retries = 3)
        {
            for (int attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
                    var task = action();
                    var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
                    if (completed == task) return await task;
                    throw new TimeoutException($"BOSS {opName} timeout 70s Boss");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"BOSS {opName} attempt {attempt} failed Boss: {ex.Message} Boss");
                    if (attempt == retries) return fallback;
                    await Task.Delay(attempt == 1 ? 2000 : attempt == 2 ? 5000 : 10000);
                }
            }
            return fallback;
        }

        // ================= REGISTER BUSINESS Boss - KEPT 100% + SEALED =================
        public async Task<AuthResult> RegisterBusinessAsync(string businessName, string phone, string location, string email, string password)
        {
            if (string.IsNullOrWhiteSpace(businessName)) return Fail("Please enter business name Boss");
            if (string.IsNullOrWhiteSpace(phone) || phone.Trim().Length < 9) return Fail("Valid phone required Boss, 9 digits Boss");
            if (string.IsNullOrWhiteSpace(location)) return Fail("Please enter location Boss, e.g. Accra Ghana Boss");
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@")) return Fail("Valid email required Boss");
            if (string.IsNullOrWhiteSpace(password) || password.Length < 6) return Fail("Password 6 chars minimum Boss");

            email = email.Trim().ToLower();

            try
            {
                if (await _context.Users.AnyAsync(u => u.Email == email))
                    return Fail("This email already exists Boss. Please login Boss");

                string firebaseUid = "";
                try
                {
                    if (_firebaseService != null && _firebaseService.FirebaseAuth != null)
                    {
                        var fbUser = await ExecuteWithRetryAsync(async () =>
                        {
                            return await _firebaseService.FirebaseAuth.CreateUserAsync(new UserRecordArgs
                            {
                                Email = email,
                                Password = password,
                                DisplayName = businessName,
                                EmailVerified = false
                            });
                        }, null!, "Firebase Create Business", 2);

                        if (fbUser != null)
                        {
                            firebaseUid = fbUser.Uid;
                            _logger.LogInformation($"Firebase user created Boss: {firebaseUid}");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("FirebaseAuth is null Boss, skipping Firebase creation Boss, using SQLite only Boss");
                    }
                }
                catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.EmailAlreadyExists)
                {
                    return Fail("This email already exists Boss. Please login Boss");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Firebase Auth create failed Boss, but continue with SQLite Boss: {ex.Message}");
                }

                string hash = BCrypt.Net.BCrypt.HashPassword(password);

                var user = new User
                {
                    Email = email,
                    PasswordHash = hash,
                    Role = "business",
                    BusinessName = businessName.Trim(),
                    FullName = businessName.Trim(),
                    Phone = phone.Trim(),
                    Location = location.Trim(),
                    FirebaseUid = firebaseUid,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Users.Add(user);
                await _context.SaveChangesAsync();
                _logger.LogInformation($"Business user saved to SQLite Boss: {user.Id} {email}");

                try
                {
                    if (_firebaseService != null && _firebaseService.IsInitialized && _firebaseService.FirestoreDb != null)
                    {
                        await ExecuteWithRetryAsync(async () =>
                        {
                            var doc = _firebaseService.FirestoreDb.Collection("users").Document(user.Id.ToString());
                            await doc.SetAsync(new Dictionary<string, object>
                            {
                                { "id", user.Id },
                                { "firebaseUid", firebaseUid },
                                { "email", email },
                                { "role", "business" },
                                { "businessName", businessName },
                                { "phone", phone },
                                { "location", location },
                                { "createdAt", Google.Cloud.Firestore.Timestamp.GetCurrentTimestamp() }
                            });
                            return true;
                        }, false, $"Firestore Save Business {user.Id}", 2);
                        _logger.LogInformation($"Business user saved to Firestore Boss: {user.Id}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Firestore save failed Boss, but SQL ok Boss: {ex.Message}");
                }

                var appUser = ToAppUser(user);
                try
                {
                    appUser.Token = GenerateJwt(user);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"JWT generation failed Boss: {ex.Message}");
                    return Fail($"Server config error Boss: JWT Key missing on Render Boss. Please set Jwt:Key in Render Env Boss. Real error: {ex.Message}");
                }

                return new AuthResult { Success = true, Message = "Welcome to IRHAM TRADE Boss! Business account ready Boss!", User = appUser };
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, $"DB Update error Boss RegisterBusiness: {ex.InnerException?.Message}");
                return Fail($"Database error Boss: {ex.InnerException?.Message ?? ex.Message} Boss. Try again Boss.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"RegisterBusiness REAL ERROR Boss: {ex.Message} | Inner: {ex.InnerException?.Message} | Stack: {ex.StackTrace}");
                var realMsg = ex.InnerException?.Message ?? ex.Message;
                return Fail($"Could not create account Boss. Real reason Boss: {realMsg} Boss. Type: {ex.GetType().Name} Boss");
            }
        }

        // ================= REGISTER BUYER Boss - KEPT 100% + SEALED =================
        public async Task<AuthResult> RegisterBuyerAsync(string fullName, string phone, string location, string email, string password)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return Fail("Full name required Boss");
            if (string.IsNullOrWhiteSpace(phone) || phone.Trim().Length < 9) return Fail("Valid phone required Boss");
            if (string.IsNullOrWhiteSpace(location)) return Fail("Location required Boss");
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@")) return Fail("Valid email required Boss");
            if (string.IsNullOrWhiteSpace(password) || password.Length < 6) return Fail("Password 6 chars minimum Boss");

            email = email.Trim().ToLower();

            try
            {
                if (await _context.Users.AnyAsync(u => u.Email == email))
                    return Fail("This email already exists Boss. Please login Boss");

                string firebaseUid = "";
                try
                {
                    if (_firebaseService != null && _firebaseService.FirebaseAuth != null)
                    {
                        var fbUser = await ExecuteWithRetryAsync(async () =>
                        {
                            return await _firebaseService.FirebaseAuth.CreateUserAsync(new UserRecordArgs
                            {
                                Email = email,
                                Password = password,
                                DisplayName = fullName
                            });
                        }, null!, "Firebase Create Buyer", 2);

                        if (fbUser != null) firebaseUid = fbUser.Uid;
                    }
                }
                catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.EmailAlreadyExists)
                {
                    return Fail("This email already exists Boss");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Firebase create failed Boss: {ex.Message}");
                }

                string hash = BCrypt.Net.BCrypt.HashPassword(password);

                var user = new User
                {
                    Email = email,
                    PasswordHash = hash,
                    Role = "buyer",
                    FullName = fullName.Trim(),
                    BusinessName = "",
                    Phone = phone.Trim(),
                    Location = location.Trim(),
                    FirebaseUid = firebaseUid,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Users.Add(user);
                await _context.SaveChangesAsync();

                try
                {
                    if (_firebaseService != null && _firebaseService.IsInitialized && _firebaseService.FirestoreDb != null)
                    {
                        await ExecuteWithRetryAsync(async () =>
                        {
                            var doc = _firebaseService.FirestoreDb.Collection("users").Document(user.Id.ToString());
                            await doc.SetAsync(new Dictionary<string, object>
                            {
                                { "id", user.Id },
                                { "firebaseUid", firebaseUid },
                                { "email", email },
                                { "role", "buyer" },
                                { "fullName", fullName },
                                { "phone", phone },
                                { "location", location },
                                { "createdAt", Google.Cloud.Firestore.Timestamp.GetCurrentTimestamp() }
                            });
                            return true;
                        }, false, $"Firestore Save Buyer {user.Id}", 2);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Firestore save failed Boss: {ex.Message}");
                }

                var appUser = ToAppUser(user);
                try
                {
                    appUser.Token = GenerateJwt(user);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"JWT generation failed Boss buyer: {ex.Message}");
                    return Fail($"Server config error Boss: JWT missing Boss. Real: {ex.Message}");
                }

                return new AuthResult { Success = true, Message = "Welcome Boss! Buyer account created Boss!", User = appUser };
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, $"DB error RegisterBuyer Boss: {ex.InnerException?.Message}");
                return Fail($"Database error Boss: {ex.InnerException?.Message ?? ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"RegisterBuyer REAL ERROR Boss: {ex.Message} | Inner: {ex.InnerException?.Message}");
                var realMsg = ex.InnerException?.Message ?? ex.Message;
                return Fail($"Could not create buyer account Boss. Real reason Boss: {realMsg} Boss. Type: {ex.GetType().Name}");
            }
        }

        // ================= LOGIN Boss - REAL ROLE Boss - KEPT 100% + SEALED =================
        public async Task<AuthResult> LoginAsync(string email, string password)
        {
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@")) return Fail("Valid email required Boss");
            if (string.IsNullOrWhiteSpace(password)) return Fail("Password required Boss");

            email = email.Trim().ToLower();

            try
            {
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
                if (user == null)
                    return Fail("Account not found Boss. Create account first Boss");

                if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                    return Fail("Wrong password Boss. Try again Boss");

                try
                {
                    user.LastLoginAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"LastLogin update failed Boss but login continues Boss: {ex.Message}");
                }

                var appUser = ToAppUser(user);
                try
                {
                    appUser.Token = GenerateJwt(user);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"JWT login failed Boss: {ex.Message}");
                    return Fail($"Server JWT config error Boss: {ex.Message}");
                }

                return new AuthResult { Success = true, Message = "Login success Boss! Opening your account Boss!", User = appUser };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Login REAL ERROR Boss: {ex.Message} | Inner: {ex.InnerException?.Message}");
                var realMsg = ex.InnerException?.Message ?? ex.Message;
                return Fail($"Login failed Boss. Real reason Boss: {realMsg} Boss");
            }
        }

        // ================= HELPERS Boss - KEPT 100% =================
        private string GenerateJwt(User user)
        {
            var key = _config["Jwt:Key"] ?? throw new Exception("Jwt:Key is missing in appsettings / Render Env Boss!");
            if (key.Length < 32) throw new Exception($"Jwt:Key too short Boss: {key.Length} chars, need 32+ Boss");

            var issuer = _config["Jwt:Issuer"] ?? "IrhamTrade";
            var audience = _config["Jwt:Audience"] ?? "IrhamTrade";
            var expireMinutesStr = _config["Jwt:ExpireMinutes"] ?? "1440";
            if (!int.TryParse(expireMinutesStr, out var expireMinutes)) expireMinutes = 1440;

            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("businessName", user.BusinessName?? ""),
                new Claim("fullName", user.FullName?? ""),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(expireMinutes),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private AppUser ToAppUser(User u) => new AppUser
        {
            Id = u.Id.ToString(),
            Role = u.Role,
            BusinessName = u.BusinessName ?? "",
            FullName = u.FullName ?? "",
            Phone = u.Phone ?? "",
            Location = u.Location ?? "",
            Email = u.Email
        };

        private AuthResult Fail(string msg) => new AuthResult { Success = false, Message = msg };
    }
}