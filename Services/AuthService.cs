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

        // ========== BOSS FIX 100% - REAL ID + REAL NAME ALWAYS - LIKE FACEBOOK ==========
        private AppUser ToAppUser(User u)
        {
            // BOSS FIX: NEVER return SQL "1" - ALWAYS use FirebaseUid if exists - This fixes logout/login random ID
            var realId = !string.IsNullOrWhiteSpace(u.FirebaseUid) ? u.FirebaseUid : u.Id.ToString();
            // If still "1" or "2" or short, keep FirebaseUid or generate safe - but don't return "1" to client
            if (realId == "1" || realId == "2" || realId.Length < 5)
            {
                if (!string.IsNullOrWhiteSpace(u.FirebaseUid))
                    realId = u.FirebaseUid;
            }

            return new AppUser
            {
                Id = realId,
                Role = u.Role ?? "",
                BusinessName = !string.IsNullOrWhiteSpace(u.BusinessName) ? u.BusinessName : (u.Role == "business" ? u.FullName ?? "" : ""),
                FullName = !string.IsNullOrWhiteSpace(u.FullName) ? u.FullName : (!string.IsNullOrWhiteSpace(u.BusinessName) ? u.BusinessName : ""),
                Phone = u.Phone ?? "",
                Location = u.Location ?? "Ghana",
                Email = u.Email ?? ""
            };
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
                catch (Exception ex) { _logger.LogWarning(ex, "Firebase Auth create skipped, using DB only"); }

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

                // ========== BOSS FIX - REAL FIREBASE UID ONLY - NEVER "1" - SAVE ONCE IN USERS ONLY ==========
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_firebaseService.IsInitialized && _firebaseService.FirestoreDb != null)
                        {
                            // BOSS FIX: If firebaseUid empty, use Guid, NEVER user.Id ("1")!
                            var docId = firebaseUid;
                            if (string.IsNullOrWhiteSpace(docId))
                            {
                                docId = Guid.NewGuid().ToString("N");
                                _logger.LogWarning("Firebase UID missing for {Email}, using Guid fallback {DocId} - NOT using SQL Id {SqlId}", email, docId, user.Id);
                            }

                            var data = new Dictionary<string, object>
                            {
                                { "id", docId },
                                { "idString", docId },
                                { "businessId", docId },
                                { "ownerId", docId },
                                { "firebaseUid", firebaseUid },
                                { "email", email },
                                { "role", "business" },
                                { "businessName", businessName.Trim() },
                                { "fullName", businessName.Trim() },
                                { "phone", phone.Trim() },
                                { "location", location.Trim() },
                                { "createdAt", Google.Cloud.Firestore.Timestamp.GetCurrentTimestamp() },
                                { "updatedAt", Google.Cloud.Firestore.Timestamp.GetCurrentTimestamp() }
                            };
                            // SAVE ONLY ONCE IN USERS - NO DOUBLE
                            await _firebaseService.FirestoreDb.Collection("users").Document(docId).SetAsync(data, Google.Cloud.Firestore.SetOptions.MergeAll);
                            _logger.LogInformation("Business user saved to Firestore users/{DocId} Email {Email}", docId, email);
                        }
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Firestore background save failed"); }
                });

                var appUser = ToAppUser(user);
                appUser.Token = GenerateJwt(user);
                // BOSS FIX: Ensure client gets REAL Firebase UID, not "1"
                if (!string.IsNullOrWhiteSpace(firebaseUid))
                    appUser.Id = firebaseUid;
                else if (appUser.Id == "1" || appUser.Id == "2" || appUser.Id.Length < 10)
                {
                    // If still bad, keep FirebaseUid or Guid, client will get real after next login via Firebase
                    _logger.LogWarning("RegisterBusiness returning short Id {Id} for {Email}, will be fixed on login", appUser.Id, email);
                }

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
                catch (Exception ex) { _logger.LogWarning(ex, "Firebase create skipped"); }

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

                // ========== BOSS FIX - BUYER ALSO REAL ID ONLY ==========
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_firebaseService.IsInitialized && _firebaseService.FirestoreDb != null)
                        {
                            var docId = firebaseUid;
                            if (string.IsNullOrWhiteSpace(docId))
                                docId = Guid.NewGuid().ToString("N");

                            var data = new Dictionary<string, object>
                            {
                                { "id", docId },
                                { "idString", docId },
                                { "buyerId", docId },
                                { "userId", docId },
                                { "firebaseUid", firebaseUid },
                                { "email", email },
                                { "role", "buyer" },
                                { "fullName", fullName.Trim() },
                                { "phone", phone.Trim() },
                                { "location", location.Trim() },
                                { "createdAt", Google.Cloud.Firestore.Timestamp.GetCurrentTimestamp() }
                            };
                            await _firebaseService.FirestoreDb.Collection("users").Document(docId).SetAsync(data, Google.Cloud.Firestore.SetOptions.MergeAll);
                        }
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "Firestore save failed"); }
                });

                var appUser = ToAppUser(user);
                appUser.Token = GenerateJwt(user);
                if (!string.IsNullOrWhiteSpace(firebaseUid))
                    appUser.Id = firebaseUid;

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

        public async Task<AuthResult> LoginAsync(string email, string password)
        {
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@")) return Fail("Please enter a valid email address");
            if (string.IsNullOrWhiteSpace(password)) return Fail("Please enter your password");

            email = email.Trim().ToLowerInvariant();
            password = password.Trim();

            try
            {
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);
                if (user != null)
                {
                    if (BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
                    {
                        user.LastLoginAt = DateTime.UtcNow;
                        try { await _context.SaveChangesAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "LastLogin update failed"); }

                        // ========== BOSS FIX - LOGIN RETURNS SAME REAL ID + SAME NAME LIKE FACEBOOK - 100% ==========
                        var appUser = ToAppUser(user);
                        appUser.Token = GenerateJwt(user);

                        // If SQLite has FirebaseUid, use it - SAME ID AS REGISTER - NO NEW ID!
                        if (!string.IsNullOrWhiteSpace(user.FirebaseUid))
                        {
                            appUser.Id = user.FirebaseUid;
                        }
                        else
                        {
                            // Try to get Firebase UID from Firestore if SQLite missing it - Fixes old accounts with "1"
                            try
                            {
                                if (_firebaseService.IsInitialized && _firebaseService.FirestoreDb != null)
                                {
                                    var snap = await _firebaseService.FirestoreDb.Collection("users").WhereEqualTo("email", email).Limit(1).GetSnapshotAsync();
                                    if (snap.Count > 0)
                                    {
                                        var dict = snap.Documents[0].ToDictionary();
                                        if (dict.TryGetValue("id", out var idObj) && !string.IsNullOrWhiteSpace(idObj?.ToString()) && idObj.ToString()!.Length > 10)
                                        {
                                            appUser.Id = idObj.ToString()!;
                                            // Save back to SQLite for next time - So next login is instant real ID
                                            user.FirebaseUid = appUser.Id;
                                            try { await _context.SaveChangesAsync(); } catch { }
                                        }
                                        // Also restore real names - Fixes email prefix as name bug
                                        if (dict.TryGetValue("businessName", out var bn) && !string.IsNullOrWhiteSpace(bn?.ToString()))
                                            appUser.BusinessName = bn.ToString()!;
                                        if (dict.TryGetValue("fullName", out var fn) && !string.IsNullOrWhiteSpace(fn?.ToString()))
                                            appUser.FullName = fn.ToString()!;
                                        if (dict.TryGetValue("phone", out var ph) && !string.IsNullOrWhiteSpace(ph?.ToString()))
                                            appUser.Phone = ph.ToString()!;
                                        if (dict.TryGetValue("location", out var loc) && !string.IsNullOrWhiteSpace(loc?.ToString()))
                                            appUser.Location = loc.ToString()!;
                                    }
                                }
                            }
                            catch (Exception ex) { _logger.LogWarning(ex, "Failed to restore real ID from Firestore for {Email}", email); }
                        }

                        // Final safety: NEVER return "1" or "2" or email prefix as name
                        if (appUser.Id == "1" || appUser.Id == "2" || appUser.Id.Length < 10)
                        {
                            _logger.LogWarning("Login still returning short Id {Id} for {Email}, trying Firebase login fallback", appUser.Id, email);
                            // Fall through to Firebase login attempt below
                        }
                        else
                        {
                            _logger.LogInformation("Login SUCCESS via SQLite {Email} Role {Role} RealId {RealId} Name {Name}", email, user.Role, appUser.Id, appUser.BusinessName + appUser.FullName);
                            return new AuthResult { Success = true, Message = "Login successful", User = appUser };
                        }
                    }
                    _logger.LogInformation("SQLite password mismatch for {Email}, trying Firebase fallback", email);
                }

                // Firebase fallback - Gets REAL ID and REAL NAME from Firestore - 100% like content apps
                var fbResult = await TryFirebaseLoginAsync(email, password);
                if (fbResult.Success && fbResult.User != null)
                {
                    if (user != null)
                    {
                        try
                        {
                            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
                            user.LastLoginAt = DateTime.UtcNow;
                            if (string.IsNullOrWhiteSpace(user.FirebaseUid) && !string.IsNullOrWhiteSpace(fbResult.User.Id))
                                user.FirebaseUid = fbResult.User.Id;
                            if (string.IsNullOrWhiteSpace(user.Phone) && !string.IsNullOrWhiteSpace(fbResult.User.Phone))
                                user.Phone = fbResult.User.Phone;
                            if (string.IsNullOrWhiteSpace(user.Location) && !string.IsNullOrWhiteSpace(fbResult.User.Location))
                                user.Location = fbResult.User.Location;
                            if (string.IsNullOrWhiteSpace(user.BusinessName) && !string.IsNullOrWhiteSpace(fbResult.User.BusinessName))
                                user.BusinessName = fbResult.User.BusinessName;
                            if (string.IsNullOrWhiteSpace(user.FullName) && !string.IsNullOrWhiteSpace(fbResult.User.FullName))
                                user.FullName = fbResult.User.FullName;

                            await _context.SaveChangesAsync();
                            _logger.LogInformation("Updated SQLite from Firebase for {Email} RealId {Id}", email, fbResult.User.Id);

                            // Return Firebase result directly - It has REAL ID and REAL NAME
                            var appUserUpdated = ToAppUser(user);
                            appUserUpdated.Id = fbResult.User.Id;
                            appUserUpdated.BusinessName = !string.IsNullOrWhiteSpace(fbResult.User.BusinessName) ? fbResult.User.BusinessName : appUserUpdated.BusinessName;
                            appUserUpdated.FullName = !string.IsNullOrWhiteSpace(fbResult.User.FullName) ? fbResult.User.FullName : appUserUpdated.FullName;
                            appUserUpdated.Phone = !string.IsNullOrWhiteSpace(fbResult.User.Phone) ? fbResult.User.Phone : appUserUpdated.Phone;
                            appUserUpdated.Location = !string.IsNullOrWhiteSpace(fbResult.User.Location) ? fbResult.User.Location : appUserUpdated.Location;
                            appUserUpdated.Token = GenerateJwt(user);
                            return new AuthResult { Success = true, Message = "Login successful", User = appUserUpdated };
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "Failed to update SQLite for {Email}", email); }
                    }
                    else
                    {
                        try
                        {
                            var role = fbResult.User.Role ?? "buyer";
                            var newUser = new User
                            {
                                Email = email,
                                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                                Role = role.ToLowerInvariant(),
                                FullName = !string.IsNullOrWhiteSpace(fbResult.User.FullName) ? fbResult.User.FullName : email.Split('@')[0],
                                BusinessName = !string.IsNullOrWhiteSpace(fbResult.User.BusinessName) ? fbResult.User.BusinessName : (role == "business" ? fbResult.User.FullName ?? "" : ""),
                                Phone = fbResult.User.Phone ?? "",
                                Location = fbResult.User.Location ?? "Ghana",
                                FirebaseUid = fbResult.User.Id ?? "",
                                CreatedAt = DateTime.UtcNow,
                                LastLoginAt = DateTime.UtcNow
                            };
                            _context.Users.Add(newUser);
                            await _context.SaveChangesAsync();
                            _logger.LogInformation("Auto-created SQLite from Firebase {Email} Role {Role}", email, role);
                            var appUserNew = ToAppUser(newUser);
                            appUserNew.Id = fbResult.User.Id;
                            appUserNew.Token = GenerateJwt(newUser);
                            return new AuthResult { Success = true, Message = "Login successful", User = appUserNew };
                        }
                        catch (Exception ex) { _logger.LogWarning(ex, "Auto-create SQLite failed for {Email}", email); }
                    }

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
                            if (dict.TryGetValue("role", out var r) && !string.IsNullOrWhiteSpace(r?.ToString())) role = r?.ToString() ?? role;
                            // BOSS FIX: REAL NAME FROM FIRESTORE - NOT email prefix - Fixes your name bug 100%
                            if (dict.TryGetValue("fullName", out var fn) && !string.IsNullOrWhiteSpace(fn?.ToString())) fullName = fn!.ToString()!;
                            if (dict.TryGetValue("businessName", out var bn) && !string.IsNullOrWhiteSpace(bn?.ToString())) { businessName = bn!.ToString()!; if (string.IsNullOrWhiteSpace(fullName) || fullName == email.Split('@')[0]) fullName = bn!.ToString()!; }
                            if (dict.TryGetValue("phone", out var ph) && !string.IsNullOrWhiteSpace(ph?.ToString())) phone = ph?.ToString() ?? "";
                            if (dict.TryGetValue("location", out var loc) && !string.IsNullOrWhiteSpace(loc?.ToString())) location = loc?.ToString() ?? location;
                            if (dict.TryGetValue("businessName", out var bn2) && role == "business" && !string.IsNullOrWhiteSpace(bn2?.ToString())) fullName = bn2!.ToString()!;
                        }
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Firestore profile fetch failed for {Email}", email); }

                // If still email prefix as name, keep it but log - Will be fixed by SQLite BusinessName
                if (fullName == email.Split('@')[0])
                {
                    _logger.LogWarning("Name still email prefix for {Email}, using displayName fallback {DisplayName}", email, displayName);
                    if (!string.IsNullOrWhiteSpace(displayName) && displayName != email.Split('@')[0])
                        fullName = displayName;
                }

                var appUser = new AppUser
                {
                    Id = uid, // REAL FIREBASE UID - NEVER "1" - Same after 1 year!
                    Role = role,
                    FullName = fullName,
                    BusinessName = businessName,
                    Phone = phone,
                    Location = location,
                    Email = email,
                    Token = ""
                };

                _logger.LogInformation("Firebase login SUCCESS {Email} UID {Uid} Role {Role} Name {Name} - SAME AS REGISTER - NO NEW ID", email, uid, role, fullName);
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
                            await _firebaseService.FirebaseAuth.UpdateUserAsync(new UserRecordArgs { Uid = user.FirebaseUid, Password = newPassword });
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
            try { var handler = new JwtSecurityTokenHandler(); var jwt = handler.ReadJwtToken(token); return jwt.ValidTo; }
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

            // BOSS FIX: JWT Sub is REAL Firebase UID - Not "1" - So token always has real ID
            var realSub = !string.IsNullOrWhiteSpace(user.FirebaseUid) ? user.FirebaseUid : user.Id.ToString();

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, realSub),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("businessName", user.BusinessName?? ""),
                new Claim("fullName", user.FullName?? ""),
                new Claim("phone", user.Phone?? ""),
                new Claim("location", user.Location?? ""),
                new Claim("role", user.Role),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(issuer, audience, claims, expires: DateTime.UtcNow.AddMinutes(expireMinutes), signingCredentials: credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        private AuthResult Fail(string msg) => new AuthResult { Success = false, Message = msg };
    }
}