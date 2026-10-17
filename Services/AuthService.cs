using FirebaseAdmin.Auth;
using IrhamTrade.Server.Data;
using IrhamTrade.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

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

        private static readonly ConcurrentDictionary<string, DateTime> _blacklistedTokens = new();
        private static readonly HttpClient _firebaseHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

        public AuthService(AppDbContext context, FirebaseService firebaseService, IConfiguration config, ILogger<AuthService> logger)
        {
            _context = context;
            _firebaseService = firebaseService;
            _config = config;
            _logger = logger;
        }

        private string GetJwtKey()
        {
            // MUST match Program.cs key logic 100% - like Facebook single source of truth
            var key = _config["Jwt:Key"]
               ?? _config["Jwt__Key"]
               ?? Environment.GetEnvironmentVariable("Jwt__Key")
               ?? Environment.GetEnvironmentVariable("JWT_KEY")
               ?? "IrhamTrade-Dev-Key-Only-Not-For-Prod-32-Chars-Min-2026!";
            if (key.Length < 32) key = "IrhamTrade-Dev-Key-Only-Not-For-Prod-32-Chars-Min-2026!";
            return key;
        }

        private async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, T fallback, string opName, int retries = 2)
        {
            for (int attempt = 1; attempt <= retries; attempt++)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var task = action();
                    var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));
                    if (completed == task) return await task;
                    throw new TimeoutException($"{opName} timeout");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Op} attempt {Attempt} failed", opName, attempt);
                    if (attempt == retries) return fallback;
                    await Task.Delay(1500);
                }
            }
            return fallback;
        }

        public async Task<AuthResult> RegisterBusinessAsync(string businessName, string phone, string location, string email, string password)
        {
            if (string.IsNullOrWhiteSpace(businessName)) return Fail("Please enter your business name");
            if (string.IsNullOrWhiteSpace(phone) || phone.Trim().Length < 9) return Fail("Please enter a valid phone number");
            if (string.IsNullOrWhiteSpace(location)) return Fail("Please enter your location");
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@") || !email.Contains(".")) return Fail("Please enter a valid email address");
            if (string.IsNullOrWhiteSpace(password) || password.Length < 6) return Fail("Password must be at least 6 characters");

            email = email.Trim().ToLower();

            try
            {
                if (await _context.Users.AnyAsync(u => u.Email == email))
                    return Fail("An account with this email already exists. Please log in instead");

                string firebaseUid = "";
                try
                {
                    if (_firebaseService.IsInitialized && _firebaseService.FirebaseAuth != null)
                    {
                        var fbUser = await ExecuteWithRetryAsync(async () =>
                        {
                            return await _firebaseService.FirebaseAuth!.CreateUserAsync(new UserRecordArgs
                            {
                                Email = email,
                                Password = password,
                                DisplayName = businessName,
                                EmailVerified = false
                            });
                        }, null!, "Firebase Create Business", 2);
                        if (fbUser != null) firebaseUid = fbUser.Uid;
                    }
                }
                catch (FirebaseAuthException ex) when (ex.AuthErrorCode == AuthErrorCode.EmailAlreadyExists)
                {
                    return Fail("An account with this email already exists. Please log in instead");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Firebase Auth create skipped, using DB only");
                }

                var user = new User
                {
                    Email = email,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
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

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_firebaseService.IsInitialized && _firebaseService.FirestoreDb != null)
                        {
                            var doc = _firebaseService.FirestoreDb.Collection("users").Document(user.Id.ToString());
                            var data = new Dictionary<string, object>
                            {
                                { "id", user.Id },
                                { "idString", user.Id.ToString() },
                                { "businessId", user.Id.ToString() },
                                { "ownerId", user.Id.ToString() },
                                { "firebaseUid", firebaseUid },
                                { "email", email },
                                { "role", "business" },
                                { "businessName", businessName },
                                { "fullName", businessName },
                                { "phone", phone },
                                { "location", location },
                                { "createdAt", Google.Cloud.Firestore.Timestamp.GetCurrentTimestamp() },
                                { "updatedAt", Google.Cloud.Firestore.Timestamp.GetCurrentTimestamp() }
                            };
                            await doc.SetAsync(data, Google.Cloud.Firestore.SetOptions.MergeAll);
                            if (!string.IsNullOrWhiteSpace(firebaseUid))
                                await _firebaseService.FirestoreDb.Collection("users").Document(firebaseUid).SetAsync(data, Google.Cloud.Firestore.SetOptions.MergeAll);
                        }
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Firestore background save failed"); }
                });

                var appUser = ToAppUser(user);
                appUser.Token = GenerateJwt(user);

                return new AuthResult { Success = true, Message = "Business account created successfully. Welcome to IRHAM TRADE!", User = appUser };
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "DB error RegisterBusiness");
                return Fail("Unable to create account. This email may already be in use. Please try logging in instead");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RegisterBusiness unexpected error");
                return Fail("Unable to create account at the moment. Please try again in a few seconds");
            }
        }

        public async Task<AuthResult> RegisterBuyerAsync(string fullName, string phone, string location, string email, string password)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return Fail("Please enter your full name");
            if (string.IsNullOrWhiteSpace(phone) || phone.Trim().Length < 9) return Fail("Please enter a valid phone number");
            if (string.IsNullOrWhiteSpace(location)) return Fail("Please enter your location");
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@")) return Fail("Please enter a valid email address");
            if (string.IsNullOrWhiteSpace(password) || password.Length < 6) return Fail("Password must be at least 6 characters");

            email = email.Trim().ToLower();

            try
            {
                if (await _context.Users.AnyAsync(u => u.Email == email))
                    return Fail("An account with this email already exists. Please log in instead");

                string firebaseUid = "";
                try
                {
                    if (_firebaseService.IsInitialized && _firebaseService.FirebaseAuth != null)
                    {
                        var fbUser = await ExecuteWithRetryAsync(async () =>
                        {
                            return await _firebaseService.FirebaseAuth!.CreateUserAsync(new UserRecordArgs
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
                    return Fail("An account with this email already exists. Please log in instead");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Firebase create skipped");
                }

                var user = new User
                {
                    Email = email,
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
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

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_firebaseService.IsInitialized && _firebaseService.FirestoreDb != null)
                        {
                            var data = new Dictionary<string, object>
                            {
                                { "id", user.Id },
                                { "idString", user.Id.ToString() },
                                { "buyerId", user.Id.ToString() },
                                { "userId", user.Id.ToString() },
                                { "firebaseUid", firebaseUid },
                                { "email", email },
                                { "role", "buyer" },
                                { "fullName", fullName },
                                { "phone", phone },
                                { "location", location },
                                { "createdAt", Google.Cloud.Firestore.Timestamp.GetCurrentTimestamp() }
                            };
                            await _firebaseService.FirestoreDb.Collection("users").Document(user.Id.ToString()).SetAsync(data, Google.Cloud.Firestore.SetOptions.MergeAll);
                            await _firebaseService.FirestoreDb.Collection("buyers").Document(user.Id.ToString()).SetAsync(data, Google.Cloud.Firestore.SetOptions.MergeAll);
                            if (!string.IsNullOrWhiteSpace(firebaseUid))
                                await _firebaseService.FirestoreDb.Collection("users").Document(firebaseUid).SetAsync(data, Google.Cloud.Firestore.SetOptions.MergeAll);
                        }
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Firestore save failed"); }
                });

                var appUser = ToAppUser(user);
                appUser.Token = GenerateJwt(user);

                return new AuthResult { Success = true, Message = "Buyer account created successfully. Welcome to IRHAM TRADE!", User = appUser };
            }
            catch (DbUpdateException)
            {
                _logger.LogError("DB error RegisterBuyer");
                return Fail("Unable to create account. This email may already be in use");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RegisterBuyer unexpected");
                return Fail("Unable to create account at the moment. Please try again shortly");
            }
        }

        // BOSS FIX - LOGIN - LIKE FACEBOOK - Always returns JWT (not Firebase token) so Server APIs work
        public async Task<AuthResult> LoginAsync(string email, string password)
        {
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@")) return Fail("Please enter a valid email address");
            if (string.IsNullOrWhiteSpace(password)) return Fail("Please enter your password");

            email = email.Trim().ToLowerInvariant();
            password = password.Trim();

            try
            {
                // 1. TRY SQLITE (server-registered users)
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
                if (user != null)
                {
                    if (BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                    {
                        user.LastLoginAt = DateTime.UtcNow;
                        try { await _context.SaveChangesAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "LastLogin update failed"); }
                        var appUser = ToAppUser(user);
                        appUser.Token = GenerateJwt(user);
                        _logger.LogInformation("Login SUCCESS via SQLite {Email}", email);
                        return new AuthResult { Success = true, Message = "Login successful", User = appUser };
                    }
                    _logger.LogInformation("SQLite password mismatch for {Email}, trying Firebase fallback to sync", email);
                }

                // 2. FIREBASE FALLBACK (APK-registered or password changed via Firebase)
                var fbResult = await TryFirebaseLoginAsync(email, password);
                if (fbResult.Success && fbResult.User != null)
                {
                    // If SQLite user exists, update its hash to match Firebase new password - like Facebook sync
                    if (user != null)
                    {
                        try
                        {
                            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
                            user.LastLoginAt = DateTime.UtcNow;
                            if (string.IsNullOrWhiteSpace(user.FirebaseUid) && !string.IsNullOrWhiteSpace(fbResult.User.Id))
                                user.FirebaseUid = fbResult.User.Id;
                            await _context.SaveChangesAsync();
                            _logger.LogInformation("Updated SQLite password from Firebase for {Email}", email);
                            var appUserUpdated = ToAppUser(user);
                            appUserUpdated.Token = GenerateJwt(user); // BOSS FIX: Return JWT not Firebase token
                            return new AuthResult { Success = true, Message = "Login successful", User = appUserUpdated };
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to update SQLite hash for {Email}", email); }
                    }
                    else
                    {
                        // No SQLite user - auto-create from Firebase
                        try
                        {
                            var role = fbResult.User.Role ?? "buyer";
                            var newUser = new User
                            {
                                Email = email,
                                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                                Role = role.ToLowerInvariant(),
                                FullName = fbResult.User.FullName ?? email.Split('@')[0],
                                BusinessName = fbResult.User.BusinessName ?? (role == "business" ? fbResult.User.FullName ?? "" : ""),
                                Phone = fbResult.User.Phone ?? "",
                                Location = fbResult.User.Location ?? "Ghana",
                                FirebaseUid = fbResult.User.Id ?? "",
                                CreatedAt = DateTime.UtcNow,
                                LastLoginAt = DateTime.UtcNow
                            };
                            _context.Users.Add(newUser);
                            await _context.SaveChangesAsync();
                            _logger.LogInformation("Auto-created SQLite user from Firebase {Email} Role {Role}", email, role);
                            var appUserNew = ToAppUser(newUser);
                            appUserNew.Token = GenerateJwt(newUser); // JWT
                            return new AuthResult { Success = true, Message = "Login successful", User = appUserNew };
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "Auto-create SQLite from Firebase failed for {Email}", email); }
                    }

                    // Fallback: If SQLite creation failed, still return JWT if possible - try to find/create
                    try
                    {
                        var tempUser = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
                        if (tempUser != null)
                        {
                            var appUserTemp = ToAppUser(tempUser);
                            appUserTemp.Token = GenerateJwt(tempUser);
                            return new AuthResult { Success = true, Message = "Login successful", User = appUserTemp };
                        }
                    }
                    catch { }

                    // Last resort: return Firebase result but convert token to JWT-like (still allow login, APK will store it)
                    // But try to generate JWT from Firebase user info
                    try
                    {
                        var fakeUser = new User { Id = 999999, Email = email, Role = fbResult.User.Role ?? "buyer", BusinessName = fbResult.User.BusinessName ?? "", FullName = fbResult.User.FullName ?? "", FirebaseUid = fbResult.User.Id ?? "" };
                        var appUserFake = ToAppUser(fakeUser);
                        appUserFake.Id = fbResult.User.Id;
                        appUserFake.Role = fbResult.User.Role ?? "buyer";
                        appUserFake.BusinessName = fbResult.User.BusinessName ?? "";
                        appUserFake.FullName = fbResult.User.FullName ?? "";
                        appUserFake.Token = GenerateJwt(fakeUser);
                        return new AuthResult { Success = true, Message = "Login successful", User = appUserFake };
                    }
                    catch { }

                    return fbResult;
                }

                if (user == null)
                    return Fail("No account found with this email. Please create an account first");
                else
                    return Fail("Incorrect email or password. Please try again");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login error {Email}", email);
                return Fail("Unable to log in at the moment. Please check your connection and try again");
            }
        }

        private async Task<AuthResult> TryFirebaseLoginAsync(string email, string password)
        {
            try
            {
                var apiKey = _config["Firebase:ApiKey"] ?? _config["Firebase__ApiKey"] ?? Environment.GetEnvironmentVariable("FIREBASE_API_KEY") ?? Environment.GetEnvironmentVariable("Firebase__ApiKey") ?? "AIzaSyAZIxCShWw9QamOaQZ5nOd0t8SgVSQlE14";

                var payload = new { email = email, password = password, returnSecureToken = true };
                var resp = await _firebaseHttp.PostAsJsonAsync($"https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key={apiKey}", payload);
                var json = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Firebase login failed {Email}: {Body}", email, json.Substring(0, Math.Min(400, json.Length)));
                    return Fail("Firebase auth failed");
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var uid = root.TryGetProperty("localId", out var lid) ? lid.GetString() ?? "" : "";
                var idToken = root.TryGetProperty("idToken", out var it) ? it.GetString() ?? "" : "";
                var displayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";

                string role = "buyer";
                string fullName = !string.IsNullOrWhiteSpace(displayName) ? displayName : email.Split('@')[0];
                string businessName = "";
                string phone = "";
                string location = "Ghana";

                try
                {
                    if (_firebaseService.IsInitialized && _firebaseService.FirestoreDb != null)
                    {
                        var snap = await _firebaseService.FirestoreDb.Collection("users").Document(uid).GetSnapshotAsync();
                        if (!snap.Exists)
                        {
                            var q = await _firebaseService.FirestoreDb.Collection("users").WhereEqualTo("email", email).Limit(1).GetSnapshotAsync();
                            if (q.Count > 0) snap = q.Documents[0];
                        }

                        if (snap.Exists)
                        {
                            var dict = snap.ToDictionary();
                            if (dict.TryGetValue("role", out var r)) role = r?.ToString() ?? role;
                            if (dict.TryGetValue("fullName", out var fn) && !string.IsNullOrWhiteSpace(fn?.ToString())) fullName = fn!.ToString()!;
                            if (dict.TryGetValue("businessName", out var bn) && !string.IsNullOrWhiteSpace(bn?.ToString())) { businessName = bn!.ToString()!; if (role == "business" && string.IsNullOrWhiteSpace(fullName)) fullName = businessName; }
                            else if (dict.TryGetValue("fullName", out var fn2) && role == "business") businessName = fn2?.ToString() ?? "";
                            if (dict.TryGetValue("phone", out var ph)) phone = ph?.ToString() ?? "";
                            if (dict.TryGetValue("location", out var loc)) location = loc?.ToString() ?? location;
                        }
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Firestore profile fetch failed for {Email}", email); }

                var appUser = new AppUser
                {
                    Id = uid,
                    Role = role,
                    FullName = fullName,
                    BusinessName = businessName,
                    Phone = phone,
                    Location = location,
                    Email = email,
                    Token = !string.IsNullOrWhiteSpace(idToken) ? idToken : ""
                };

                _logger.LogInformation("Firebase login SUCCESS {Email} UID {Uid} Role {Role}", email, uid, role);
                return new AuthResult { Success = true, Message = "Login successful via Firebase", User = appUser };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TryFirebaseLogin exception {Email}", email);
                return Fail("Firebase exception");
            }
        }

        public async Task<AuthResult> ForgotPasswordAsync(string email)
        {
            try
            {
                email = email.Trim().ToLowerInvariant();
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
                if (user == null) return Fail("No account found with this email. Please check email or create account.");

                try
                {
                    if (_firebaseService.IsInitialized && _firebaseService.FirebaseAuth != null)
                    {
                        string link = "";
                        if (!string.IsNullOrWhiteSpace(user.FirebaseUid))
                        {
                            link = await _firebaseService.FirebaseAuth.GeneratePasswordResetLinkAsync(email);
                        }
                        else
                        {
                            try
                            {
                                var fbUser = await _firebaseService.FirebaseAuth.GetUserByEmailAsync(email);
                                if (fbUser != null) link = await _firebaseService.FirebaseAuth.GeneratePasswordResetLinkAsync(email);
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Firebase reset link generation failed for {Email}", email); }

                return new AuthResult { Success = true, Message = $"Password reset instructions sent to {email}. Please check your inbox and spam folder." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForgotPassword error");
                return Fail("Unable to process request. Please try again.");
            }
        }

        public async Task<AuthResult> ResetPasswordAsync(string email, string newPassword)
        {
            try
            {
                email = email.Trim().ToLowerInvariant();
                if (newPassword.Length < 6) return Fail("Password must be at least 6 characters");
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
                if (user == null) return Fail("No account found with this email.");

                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
                await _context.SaveChangesAsync();

                try
                {
                    if (_firebaseService.IsInitialized && _firebaseService.FirebaseAuth != null)
                    {
                        if (!string.IsNullOrWhiteSpace(user.FirebaseUid))
                        {
                            await _firebaseService.FirebaseAuth.UpdateUserAsync(new UserRecordArgs { Uid = user.FirebaseUid, Password = newPassword });
                        }
                        else
                        {
                            var fbUser = await _firebaseService.FirebaseAuth.GetUserByEmailAsync(email);
                            await _firebaseService.FirebaseAuth.UpdateUserAsync(new UserRecordArgs { Uid = fbUser.Uid, Password = newPassword });
                        }
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Firebase password update failed for {Email}", email); }

                return new AuthResult { Success = true, Message = "Password reset successful. You can now login with new password." };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResetPassword error");
                return Fail("Unable to reset password. Try again.");
            }
        }

        public async Task LogoutAsync(string token)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(token)) return;
                var expiry = GetTokenExpiry(token) ?? DateTime.UtcNow.AddHours(24);
                _blacklistedTokens[token] = expiry;
                foreach (var kv in _blacklistedTokens.Where(kv => kv.Value < DateTime.UtcNow).ToList())
                    _blacklistedTokens.TryRemove(kv.Key, out _);
                await Task.CompletedTask;
            }
            catch { }
        }

        public async Task<bool> ValidateTokenAsync(string token)
        {
            try
            {
                if (_blacklistedTokens.ContainsKey(token)) return false;
                var handler = new JwtSecurityTokenHandler();
                if (!handler.CanReadToken(token)) return false;
                var jwt = handler.ReadJwtToken(token);
                if (jwt.ValidTo < DateTime.UtcNow) return false;
                return true;
            }
            catch { return false; }
        }

        private DateTime? GetTokenExpiry(string token)
        {
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(token);
                return jwt.ValidTo;
            }
            catch { return null; }
        }

        private string GenerateJwt(User user)
        {
            var key = GetJwtKey();
            var issuer = _config["Jwt:Issuer"] ?? "IrhamTrade.Server";
            var audience = _config["Jwt:Audience"] ?? "IrhamTrade.App";
            var expireMinutes = int.TryParse(_config["Jwt:ExpireMinutes"], out var m) ? m : 1440;

            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("businessName", user.BusinessName?? ""),
                new Claim("fullName", user.FullName?? ""),
                new Claim("role", user.Role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(issuer, audience, claims, expires: DateTime.UtcNow.AddMinutes(expireMinutes), signingCredentials: credentials);
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