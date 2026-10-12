using FirebaseAdmin.Auth;
using FirebaseAdmin.Auth.Hash;
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
        public string Token { get; set; } = ""; // JWT Boss
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

        // ================= REGISTER BUSINESS Boss =================
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
                // Check SQLite Boss - fast Boss
                if (await _context.Users.AnyAsync(u => u.Email == email))
                    return Fail("This email already exists Boss. Please login Boss");

                // Try create in Firebase Auth Boss - for Android APK Boss
                string firebaseUid = "";
                try
                {
                    if (_firebaseService.FirebaseAuth != null)
                    {
                        var fbUser = await _firebaseService.FirebaseAuth.CreateUserAsync(new UserRecordArgs
                        {
                            Email = email,
                            Password = password,
                            DisplayName = businessName,
                            EmailVerified = false
                        });
                        firebaseUid = fbUser.Uid;
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

                // Hash with BCrypt Boss - SECURE Boss for huge users Boss
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

                // Save to Firestore Boss - for scale Boss
                try
                {
                    if (_firebaseService.IsInitialized)
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
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Firestore save failed Boss, but SQL ok Boss: {ex.Message}");
                }

                var appUser = ToAppUser(user);
                appUser.Token = GenerateJwt(user);

                return new AuthResult { Success = true, Message = "Welcome to IRHAM TRADE Boss! Business account ready Boss!", User = appUser };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RegisterBusiness error Boss");
                return Fail("Server busy Boss, try again Boss. Check internet Boss");
            }
        }

        // ================= REGISTER BUYER Boss =================
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
                    if (_firebaseService.FirebaseAuth != null)
                    {
                        var fbUser = await _firebaseService.FirebaseAuth.CreateUserAsync(new UserRecordArgs
                        {
                            Email = email,
                            Password = password,
                            DisplayName = fullName
                        });
                        firebaseUid = fbUser.Uid;
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
                    if (_firebaseService.IsInitialized)
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
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Firestore save failed Boss: {ex.Message}");
                }

                var appUser = ToAppUser(user);
                appUser.Token = GenerateJwt(user);

                return new AuthResult { Success = true, Message = "Welcome Boss! Buyer account created Boss!", User = appUser };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RegisterBuyer error Boss");
                return Fail("Server busy Boss, try again Boss");
            }
        }

        // ================= LOGIN Boss - REAL ROLE Boss =================
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

                // BCrypt verify Boss - SECURE Boss
                if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                    return Fail("Wrong password Boss. Try again Boss");

                user.LastLoginAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                var appUser = ToAppUser(user);
                appUser.Token = GenerateJwt(user);

                return new AuthResult { Success = true, Message = "Login success Boss! Opening your account Boss!", User = appUser };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login error Boss");
                return Fail("Login failed Boss. Internet slow Boss, try again Boss");
            }
        }

        // ================= HELPERS Boss =================
        private string GenerateJwt(User user)
        {
            var key = _config["Jwt:Key"]!;
            var issuer = _config["Jwt:Issuer"];
            var audience = _config["Jwt:Audience"];
            var expireMinutes = int.Parse(_config["Jwt:ExpireMinutes"] ?? "1440");

            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("businessName", user.BusinessName ?? ""),
                new Claim("fullName", user.FullName ?? ""),
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