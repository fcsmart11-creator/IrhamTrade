using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Google.Cloud.Firestore;
using System.Security.Cryptography;
using System.Text;
using IrhamTrade.Server.Services;

namespace IrhamTrade.Server.Controllers
{
    // Purpose: Support + admin control - announcements, messages, seller reports, plan upgrade requests (publish limit / top placement).
    // Admin login: ADMIN_KEY (Render setting) is used once to create the admin password. After that the admin logs in with the password.
    // Every admin action needs the session token (X-Admin-Token header) returned by the login.
    [ApiController]
    [Route("api/[controller]")]
    public class SupportController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly FirebaseService _firebase;
        private readonly IMemoryCache _cache;
        private readonly ILogger<SupportController> _logger;

        private const int PasswordIterations = 100000;
        private const int SessionHours = 12;

        public SupportController(DashboardService svc, FirebaseService firebase, IMemoryCache cache, ILogger<SupportController> logger)
        {
            _svc = svc;
            _firebase = firebase;
            _cache = cache;
            _logger = logger;
        }

        // ===== small helpers =====
        private static string GetString(Dictionary<string, object> data, string key)
        {
            try
            {
                if (data.TryGetValue(key, out var v) && v != null)
                {
                    if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
                        return je.GetString()?.Trim() ?? "";
                    return v.ToString()?.Trim() ?? "";
                }
                foreach (var kv in data)
                    if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && kv.Value != null)
                        return kv.Value.ToString()?.Trim() ?? "";
            }
            catch { }
            return "";
        }

        // Same as GetString but does NOT trim (used for passwords)
        private static string Raw(Dictionary<string, object>? d, string key)
        {
            try
            {
                if (d != null && d.TryGetValue(key, out var v) && v != null)
                {
                    if (v is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.String)
                        return je.GetString() ?? "";
                    return v.ToString() ?? "";
                }
            }
            catch { }
            return "";
        }

        private static int IntOf(Dictionary<string, object>? d, string key, int fallback)
        {
            try
            {
                var s = d == null ? "" : GetString(d, key);
                if (int.TryParse(s, out var n) && n > 0) return n;
            }
            catch { }
            return fallback;
        }

        private static int EnvInt(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name)?.Trim(), out var n) && n > 0 ? n : fallback;

        private static string EnvStr(string name, string fallback = "")
        {
            var v = Environment.GetEnvironmentVariable(name)?.Trim();
            return string.IsNullOrWhiteSpace(v) ? fallback : v;
        }

        private static DateTime? DateOf(object? v)
        {
            if (v is Timestamp t) return t.ToDateTime();
            if (v is DateTime d) return d.ToUniversalTime();
            return null;
        }

        private static Timestamp Ts(DateTime utc) => Timestamp.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

        private static string MaskBusinessId(string rawId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rawId)) return "IRH-****-XXXX";
                rawId = rawId.Trim();
                if (rawId.Length <= 4) return $"IRH-****-{rawId.ToUpper()}";
                var last4 = rawId.Substring(rawId.Length - 4).ToUpper();
                return $"IRH-****-{last4}";
            }
            catch { return "IRH-****-XXXX"; }
        }

        private static string DescribePlan(Dictionary<string, object> d)
        {
            var now = DateTime.UtcNow;
            var parts = new List<string>();
            long limit = 0;
            if (d.TryGetValue("publishLimit", out var pl) && pl != null) long.TryParse(pl.ToString(), out limit);
            var exp = d.TryGetValue("limitExpiresAt", out var le) ? DateOf(le) : null;
            if (limit > DashboardService.FreeLimit && (!exp.HasValue || exp.Value > now))
                parts.Add($"Limit {limit}" + (exp.HasValue ? $" until {exp.Value:yyyy-MM-dd}" : ""));
            else
                parts.Add($"Free plan ({DashboardService.FreeLimit})");
            var fu = d.TryGetValue("featuredUntil", out var f) ? DateOf(f) : null;
            if (fu.HasValue && fu.Value > now) parts.Add($"Top until {fu.Value:yyyy-MM-dd}");
            return string.Join(" | ", parts);
        }

        // ===== ADMIN LOGIN: key (once) -> password -> session token =====
        private sealed class AdminCreds
        {
            public string Salt = "";
            public string Hash = "";
            public int Iterations = PasswordIterations;
        }

        private static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[]? FromB64Url(string s)
        {
            try
            {
                s = s.Replace('-', '+').Replace('_', '/');
                switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
                return Convert.FromBase64String(s);
            }
            catch { return null; }
        }

        private static string? AdminKey()
        {
            var k = Environment.GetEnvironmentVariable("ADMIN_KEY")?.Trim();
            return string.IsNullOrWhiteSpace(k) || k.Length < 16 ? null : k;
        }

        private static bool KeyMatches(string given)
        {
            var expected = AdminKey();
            if (expected == null) return false;
            var a = Encoding.UTF8.GetBytes(given ?? "");
            var b = Encoding.UTF8.GetBytes(expected);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static bool PasswordOk(string p) =>
            !string.IsNullOrEmpty(p) && p.Length >= 10 && p.Length <= 100 && p.Any(char.IsLetter) && p.Any(char.IsDigit);

        private static byte[] Derive(string password, byte[] salt, int iterations) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

        private async Task<AdminCreds?> LoadCredsAsync()
        {
            if (_cache.TryGetValue("admin_creds", out AdminCreds? cached) && cached != null) return cached;
            var snap = await _firebase.FirestoreDb.Collection("admin_config").Document("credentials").GetSnapshotAsync();
            if (!snap.Exists) return null;
            var d = snap.ToDictionary();
            var c = new AdminCreds { Salt = GetString(d, "salt"), Hash = GetString(d, "hash") };
            if (d.TryGetValue("iterations", out var it) && it != null && int.TryParse(it.ToString(), out var n) && n > 0) c.Iterations = n;
            if (string.IsNullOrWhiteSpace(c.Salt) || string.IsNullOrWhiteSpace(c.Hash)) return null;
            _cache.Set("admin_creds", c, TimeSpan.FromSeconds(60));
            return c;
        }

        private async Task SavePasswordAsync(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var hash = Derive(password, salt, PasswordIterations);
            await _firebase.FirestoreDb.Collection("admin_config").Document("credentials").SetAsync(new Dictionary<string, object>
            {
                ["salt"] = Convert.ToBase64String(salt),
                ["hash"] = Convert.ToBase64String(hash),
                ["iterations"] = (long)PasswordIterations,
                ["updatedAt"] = Timestamp.GetCurrentTimestamp()
            });
            _cache.Remove("admin_creds");
        }

        private static bool PasswordMatches(string password, AdminCreds c)
        {
            try
            {
                var salt = Convert.FromBase64String(c.Salt);
                var expected = Convert.FromBase64String(c.Hash);
                var actual = Derive(password ?? "", salt, c.Iterations);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch { return false; }
        }

        // The token is signed with a secret built from ADMIN_KEY + the password hash, so changing either one ends every old session
        private static byte[] Sign(byte[] data, string key, AdminCreds c)
        {
            var secret = SHA256.HashData(Encoding.UTF8.GetBytes(key + "|" + c.Hash));
            using var h = new HMACSHA256(secret);
            return h.ComputeHash(data);
        }

        private static string MakeToken(AdminCreds c)
        {
            var key = AdminKey() ?? "";
            var exp = DateTimeOffset.UtcNow.AddHours(SessionHours).ToUnixTimeSeconds();
            var payload = exp.ToString() + "." + B64Url(RandomNumberGenerator.GetBytes(8));
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            return B64Url(payloadBytes) + "." + B64Url(Sign(payloadBytes, key, c));
        }

        private static bool TokenValid(string token, AdminCreds c)
        {
            try
            {
                var key = AdminKey();
                if (key == null || string.IsNullOrWhiteSpace(token)) return false;
                var parts = token.Split('.');
                if (parts.Length != 2) return false;
                var payloadBytes = FromB64Url(parts[0]);
                var sigBytes = FromB64Url(parts[1]);
                if (payloadBytes == null || sigBytes == null) return false;
                if (!CryptographicOperations.FixedTimeEquals(sigBytes, Sign(payloadBytes, key, c))) return false;
                var payload = Encoding.UTF8.GetString(payloadBytes);
                if (!long.TryParse(payload.Split('.')[0], out var exp)) return false;
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds() < exp;
            }
            catch { return false; }
        }

        private string FailKey() => "adminfail_" + (HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        private bool IsBlocked() => _cache.TryGetValue(FailKey(), out int f) && f >= 8;
        private void RegisterFail()
        {
            var k = FailKey();
            var f = _cache.TryGetValue(k, out int x) ? x : 0;
            _cache.Set(k, f + 1, TimeSpan.FromMinutes(15));
            _logger.LogWarning("Admin login failure from {Key} (attempt {N})", k, f + 1);
        }
        private void ClearFails() => _cache.Remove(FailKey());

        private async Task<IActionResult?> CheckAdminAsync()
        {
            if (AdminKey() == null)
                return StatusCode(503, new { success = false, message = "Admin key is not set on the server. Add ADMIN_KEY (16 or more characters) in the Render settings." });
            var creds = await LoadCredsAsync();
            if (creds == null)
                return StatusCode(409, new { success = false, needsSetup = true, message = "Create your admin password first." });
            var token = Request.Headers["X-Admin-Token"].ToString().Trim();
            if (!TokenValid(token, creds))
                return Unauthorized(new { success = false, message = "Session ended. Please log in again." });
            return null;
        }

        private async Task AuditAsync(string action, string details)
        {
            try
            {
                var id = Guid.NewGuid().ToString("N");
                await _firebase.FirestoreDb.Collection("admin_audit").Document(id).SetAsync(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["action"] = action,
                    ["details"] = details,
                    ["ip"] = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    ["at"] = Timestamp.GetCurrentTimestamp()
                });
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Audit write failed for {Action}", action); }
        }

        // Email goes out over HTTPS through Resend (Render blocks normal email ports)
        private async Task<bool> SendGmailEmailAsync(string subject, string body)
        {
            var toEmail = Environment.GetEnvironmentVariable("SMTP_TO_EMAIL")?.Trim();
            var fromEmail = Environment.GetEnvironmentVariable("SMTP_EMAIL")?.Trim();
            if (string.IsNullOrWhiteSpace(toEmail)) toEmail = "irhamtradesupport@gmail.com";
            if (string.IsNullOrWhiteSpace(fromEmail)) fromEmail = "irhamtradesupport@gmail.com";

            var resendApiKey = Environment.GetEnvironmentVariable("RESEND_API_KEY")?.Trim()
                ?? Environment.GetEnvironmentVariable("Resend__ApiKey")?.Trim();

            if (!string.IsNullOrWhiteSpace(resendApiKey))
            {
                try
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {resendApiKey}");
                    var safeHtml = System.Net.WebUtility.HtmlEncode(body).Replace("\n", "<br/>");
                    var htmlBody = $"<div style='font-family:Arial; max-width:700px;'><h2>{System.Net.WebUtility.HtmlEncode(subject)}</h2><p>{safeHtml}</p><hr/><p style='font-size:12px;color:#888;'>From: {fromEmail} via IrhamTrade</p></div>";
                    var payload = new
                    {
                        from = "IrhamTrade <onboarding@resend.dev>",
                        to = new[] { toEmail },
                        subject = subject,
                        html = htmlBody,
                        text = body
                    };
                    var json = System.Text.Json.JsonSerializer.Serialize(payload);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");
                    var response = await httpClient.PostAsync("https://api.resend.com/emails", content);
                    var respBody = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation("EMAIL SENT via RESEND to {To} - Subject: {Sub}", toEmail, subject);
                        return true;
                    }
                    _logger.LogError("Resend FAILED for {Sub}: {Status} {Body}", subject, response.StatusCode, respBody);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Resend exception for {Sub}", subject);
                    return false;
                }
            }

            _logger.LogWarning("RESEND_API_KEY missing - saved in the database but email not sent");
            return false;
        }

        // ===== PUBLIC: notices, contact, report =====
        [HttpGet("notices")]
        public async Task<IActionResult> GetNotices()
        {
            try { var notices = await _svc.GetSafetyNoticesAsync(); return Ok(notices); }
            catch (Exception ex) { _logger.LogError(ex, "Support notices failed"); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpPost("contact")]
        public async Task<IActionResult> Contact([FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (data == null) return BadRequest(new { success = false, message = "Message required" });
                var message = GetString(data, "message");
                if (string.IsNullOrWhiteSpace(message) || message.Length < 5)
                    return BadRequest(new { success = false, message = "Message must be at least 5 characters" });

                var buyerEmail = GetString(data, "buyerEmail");
                var buyerName = GetString(data, "buyerName");
                if (string.IsNullOrWhiteSpace(buyerName)) buyerName = GetString(data, "buyer_name");
                var buyerId = GetString(data, "buyerId");
                if (string.IsNullOrWhiteSpace(buyerId)) buyerId = GetString(data, "buyer_id");

                var db = _firebase.FirestoreDb;
                var docId = Guid.NewGuid().ToString("N");
                var now = Timestamp.GetCurrentTimestamp();
                await db.Collection("support_messages").Document(docId).SetAsync(new Dictionary<string, object>
                {
                    ["id"] = docId,
                    ["buyerId"] = buyerId,
                    ["buyerName"] = buyerName,
                    ["buyerEmail"] = buyerEmail,
                    ["message"] = message,
                    ["type"] = "support",
                    ["status"] = "new",
                    ["createdAt"] = now
                });

                var subject = $"[IrhamTrade Support] Message from {buyerName} - {buyerEmail}";
                var body = $"Support message\n\nFrom: {buyerName}\nEmail: {buyerEmail}\nBuyerId: {buyerId}\n\nMessage:\n{message}\n\nTime UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm}\nDocId: {docId}";
                await SendGmailEmailAsync(subject, body);

                return Ok(new { success = true, message = "Support message sent to irhamtradesupport@gmail.com" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Contact failed"); return Ok(new { success = true, message = "Message saved - will be reviewed" }); }
        }

        [HttpPost("report-business")]
        public async Task<IActionResult> ReportBusiness([FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (data == null) return BadRequest(new { success = false, message = "Report data required" });

                var businessName = GetString(data, "reportedBusinessName");
                if (string.IsNullOrWhiteSpace(businessName)) businessName = GetString(data, "businessName");
                if (string.IsNullOrWhiteSpace(businessName)) businessName = GetString(data, "reportedBusiness");

                var businessId = GetString(data, "reportedBusinessId");
                if (string.IsNullOrWhiteSpace(businessId)) businessId = GetString(data, "businessId");

                var product = GetString(data, "reportedProduct");
                if (string.IsNullOrWhiteSpace(product)) product = GetString(data, "productName");

                var reason = GetString(data, "reason");
                if (string.IsNullOrWhiteSpace(reason)) reason = GetString(data, "message");
                if (string.IsNullOrWhiteSpace(reason)) reason = GetString(data, "details");

                var buyerId = GetString(data, "buyerId");
                if (string.IsNullOrWhiteSpace(buyerId)) buyerId = GetString(data, "reporterId");
                var buyerName = GetString(data, "buyerName");
                var buyerEmail = GetString(data, "buyerEmail");
                var businessCodeMasked = GetString(data, "businessCodeMasked");
                if (string.IsNullOrWhiteSpace(businessCodeMasked)) businessCodeMasked = MaskBusinessId(businessId);

                if (string.IsNullOrWhiteSpace(businessName) || businessName.Length < 2)
                    return BadRequest(new { success = false, message = "Business name required" });
                if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10)
                    return BadRequest(new { success = false, message = "Reason must be at least 10 characters" });

                // The service accepts a short reason (3-60 characters); the full text goes in the details
                var shortReason = string.IsNullOrWhiteSpace(product) ? reason : $"{product} - {reason}";
                if (shortReason.Length > 60) shortReason = shortReason.Substring(0, 57) + "...";
                if (shortReason.Length < 3) shortReason = "Fraud report";

                var fullDetails = $"Product: {product}\nReason: {reason}\nReported via BuyerProfile Help\nBuyerEmail: {buyerEmail}\nBuyerName: {buyerName}\nMasked Code: {businessCodeMasked}";

                // The service saves the report in "reports" and sends ONE email to you
                var result = await _svc.SubmitSellerReportAsync(buyerId, businessId, businessName, shortReason, fullDetails);

                if (result.Ok)
                    return Ok(new { success = true, message = "Report received - it will be reviewed within 24 hours." });
                return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex) { _logger.LogError(ex, "Report business failed"); return StatusCode(500, new { success = false, message = "Could not send the report right now. Please try again." }); }
        }

        // ===== PUBLIC: plan info + upgrade requests (used by the business app) =====
        [HttpGet("plan-info")]
        public IActionResult PlanInfo()
        {
            return Ok(new
            {
                freeLimit = DashboardService.FreeLimit,
                momoNumber = EnvStr("PLAN_MOMO_NUMBER"),
                momoName = EnvStr("PLAN_MOMO_NAME"),
                limitItems = EnvInt("PLAN_LIMIT_ITEMS", 20),
                limitDays = EnvInt("PLAN_LIMIT_DAYS", 30),
                limitPrice = EnvStr("PLAN_LIMIT_PRICE"),
                featuredDays = EnvInt("PLAN_FEATURED_DAYS", 7),
                featuredPrice = EnvStr("PLAN_FEATURED_PRICE")
            });
        }

        [HttpGet("plan/{businessId}")]
        public async Task<IActionResult> BusinessPlan(string businessId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(businessId) || businessId.Length < 10) return BadRequest(new { success = false, message = "Invalid business" });
                var plan = await _svc.GetBusinessPlanAsync(businessId);
                if (plan == null) return NotFound(new { success = false, message = "Business not found" });
                return Ok(plan);
            }
            catch (Exception ex) { _logger.LogError(ex, "Plan lookup failed {Id}", businessId); return StatusCode(500, new { success = false, message = "Could not load the plan" }); }
        }

        [HttpPost("upgrade-request")]
        public async Task<IActionResult> UpgradeRequest([FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (data == null) return BadRequest(new { success = false, message = "Request data required" });
                var businessId = GetString(data, "businessId");
                var businessName = GetString(data, "businessName");
                var plan = GetString(data, "plan").ToLowerInvariant();
                var momoName = GetString(data, "momoName");
                var amount = new string(GetString(data, "amount").Where(c => char.IsDigit(c) || c == '.').ToArray());

                if (string.IsNullOrWhiteSpace(businessId) || businessId.Length < 10) return BadRequest(new { success = false, message = "Please log in again." });
                if (plan != "limit" && plan != "featured") return BadRequest(new { success = false, message = "Choose what you are paying for." });
                if (momoName.Length < 2 || momoName.Length > 60) return BadRequest(new { success = false, message = "Enter the Mobile Money name you used to send the money." });
                if (amount.Length == 0 || amount.Length > 12) return BadRequest(new { success = false, message = "Enter the amount you sent." });
                if (businessName.Length > 80) businessName = businessName.Substring(0, 80);

                var db = _firebase.FirestoreDb;
                var biz = await db.Collection("users").Document(businessId).GetSnapshotAsync();
                if (!biz.Exists) return BadRequest(new { success = false, message = "Your account could not be verified. Please log in again." });
                var bizData = biz.ToDictionary();
                if (string.IsNullOrWhiteSpace(businessName)) businessName = GetString(bizData, "businessName");
                if (string.IsNullOrWhiteSpace(businessName)) businessName = GetString(bizData, "fullName");
                if (string.IsNullOrWhiteSpace(businessName)) businessName = "Business";

                // Limit abuse: max 5 requests per day, and only one open request per plan
                var mine = await db.Collection("upgrade_requests").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
                var since = DateTime.UtcNow.AddHours(-24);
                var recent = 0;
                foreach (var doc in mine.Documents)
                {
                    var d = doc.ToDictionary();
                    var created = d.TryGetValue("createdAt", out var c) ? DateOf(c) : null;
                    if (created.HasValue && created.Value > since) recent++;
                    if (GetString(d, "status").Equals("new", StringComparison.OrdinalIgnoreCase) && GetString(d, "plan") == plan)
                        return BadRequest(new { success = false, message = "You already have a pending request for this. Please wait while we confirm your payment." });
                }
                if (recent >= 5) return BadRequest(new { success = false, message = "Too many requests today. Please try again tomorrow." });

                var id = Guid.NewGuid().ToString("N");
                await db.Collection("upgrade_requests").Document(id).SetAsync(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["businessId"] = businessId,
                    ["businessName"] = businessName,
                    ["plan"] = plan,
                    ["amount"] = amount,
                    ["momoName"] = momoName,
                    ["status"] = "new",
                    ["createdAt"] = Timestamp.GetCurrentTimestamp()
                });

                var planText = plan == "featured" ? "TOP PLACEMENT" : "MORE ITEMS (publish limit)";
                _ = SendGmailEmailAsync($"[IrhamTrade] Upgrade request: {businessName} - {planText}",
                    $"New upgrade request\n\nBusiness: {businessName}\nPlan: {planText}\nAmount: GHS {amount}\nMoMo name used: {momoName}\nBusiness ID: {businessId}\n\nCheck your Mobile Money message for this name and amount, then approve it on the admin page.\n\nTime UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm}");

                return Ok(new { success = true, id, message = "Request received. We will upgrade your account after we confirm your payment." });
            }
            catch (Exception ex) { _logger.LogError(ex, "Upgrade request failed"); return StatusCode(500, new { success = false, message = "Could not send the request right now. Please try again." }); }
        }

        // ===== ADMIN: login (key once, then password) =====
        [HttpGet("admin/status")]
        public async Task<IActionResult> AdminStatus()
        {
            try
            {
                var creds = await LoadCredsAsync();
                return Ok(new { keySet = AdminKey() != null, passwordSet = creds != null });
            }
            catch (Exception ex) { _logger.LogError(ex, "Admin status failed"); return StatusCode(500, new { success = false, message = "Could not check the admin status. Try again." }); }
        }

        // Creates or changes the admin password. Always needs the ADMIN_KEY.
        [HttpPost("admin/set-password")]
        public async Task<IActionResult> AdminSetPassword([FromBody] Dictionary<string, object> body)
        {
            try
            {
                if (AdminKey() == null)
                    return StatusCode(503, new { success = false, message = "Admin key is not set on the server. Add ADMIN_KEY (16 or more characters) in the Render settings." });
                if (IsBlocked())
                    return StatusCode(429, new { success = false, message = "Too many wrong attempts. Try again in 15 minutes." });

                var key = Raw(body, "key").Trim();
                var password = Raw(body, "password");
                if (!KeyMatches(key)) { RegisterFail(); return Unauthorized(new { success = false, message = "Wrong admin key." }); }
                if (!PasswordOk(password))
                    return BadRequest(new { success = false, message = "Password must be 10 to 100 characters and include letters and numbers." });

                await SavePasswordAsync(password);
                ClearFails();
                var creds = await LoadCredsAsync();
                if (creds == null) return StatusCode(500, new { success = false, message = "Could not save the password. Try again." });
                await AuditAsync("set-password", "Admin password created or changed");
                return Ok(new { success = true, token = MakeToken(creds), message = "Password saved." });
            }
            catch (Exception ex) { _logger.LogError(ex, "Set password failed"); return StatusCode(500, new { success = false, message = "Could not save the password. Try again." }); }
        }

        [HttpPost("admin/login")]
        public async Task<IActionResult> AdminLogin([FromBody] Dictionary<string, object> body)
        {
            try
            {
                if (AdminKey() == null)
                    return StatusCode(503, new { success = false, message = "Admin key is not set on the server. Add ADMIN_KEY (16 or more characters) in the Render settings." });
                if (IsBlocked())
                    return StatusCode(429, new { success = false, message = "Too many wrong attempts. Try again in 15 minutes." });

                var creds = await LoadCredsAsync();
                if (creds == null)
                    return StatusCode(409, new { success = false, needsSetup = true, message = "Create your admin password first." });

                var password = Raw(body, "password");
                if (!PasswordMatches(password, creds)) { RegisterFail(); return Unauthorized(new { success = false, message = "Wrong password." }); }

                ClearFails();
                await AuditAsync("login", "Admin logged in");
                return Ok(new { success = true, token = MakeToken(creds) });
            }
            catch (Exception ex) { _logger.LogError(ex, "Admin login failed"); return StatusCode(500, new { success = false, message = "Could not log in right now. Try again." }); }
        }

        [HttpGet("admin/summary")]
        public async Task<IActionResult> AdminSummary()
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var db = _firebase.FirestoreDb;
                var r = await db.Collection("upgrade_requests").WhereEqualTo("status", "new").Limit(200).GetSnapshotAsync();
                var m = await db.Collection("support_messages").WhereEqualTo("status", "new").Limit(200).GetSnapshotAsync();
                var p = await db.Collection("reports").WhereEqualTo("status", "new").Limit(200).GetSnapshotAsync();
                return Ok(new { newRequests = r.Count, newMessages = m.Count, newReports = p.Count });
            }
            catch (Exception ex) { _logger.LogError(ex, "Admin summary failed"); return StatusCode(500, new { success = false, message = "Could not load the summary" }); }
        }

        // ===== ADMIN: announcements =====
        [HttpPost("announce")]
        public async Task<IActionResult> Announce([FromBody] Dictionary<string, object> data)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                if (data == null) return BadRequest(new { success = false, message = "Data is required." });
                var title = GetString(data, "title");
                var message = GetString(data, "message");
                if (string.IsNullOrWhiteSpace(message)) message = GetString(data, "body");
                var type = GetString(data, "type");
                if (string.IsNullOrWhiteSpace(type)) type = "info";

                if (string.IsNullOrWhiteSpace(title) || title.Length < 3)
                    return BadRequest(new { success = false, message = "Title must be at least 3 characters." });
                if (string.IsNullOrWhiteSpace(message) || message.Length < 10)
                    return BadRequest(new { success = false, message = "Message must be at least 10 characters." });

                var db = _firebase.FirestoreDb;
                var docId = Guid.NewGuid().ToString("N");
                var now = Timestamp.GetCurrentTimestamp();
                await db.Collection("safety_notices").Document(docId).SetAsync(new Dictionary<string, object>
                {
                    ["id"] = docId,
                    ["title"] = title,
                    ["message"] = message,
                    ["type"] = type,
                    ["priority"] = type == "warning" || type == "alert" ? "high" : "normal",
                    ["createdAt"] = now,
                    ["createdBy"] = "admin",
                    ["isActive"] = true,
                    ["active"] = true
                });
                await AuditAsync("announce", title);
                return Ok(new { success = true, id = docId, message = "Announcement published successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish announcement");
                return StatusCode(500, new { success = false, message = "An error occurred while publishing." });
            }
        }

        [HttpGet("admin/notices")]
        public async Task<IActionResult> AdminNotices()
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var snap = await _firebase.FirestoreDb.Collection("safety_notices").Limit(100).GetSnapshotAsync();
                var list = new List<Dictionary<string, object>>();
                foreach (var doc in snap.Documents)
                {
                    var d = doc.ToDictionary();
                    if (d.TryGetValue("active", out var a) && a is bool ab && !ab) continue;
                    var message = GetString(d, "message");
                    if (string.IsNullOrWhiteSpace(message)) continue;
                    var created = d.TryGetValue("createdAt", out var c) ? DateOf(c) : null;
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = doc.Id,
                        ["title"] = GetString(d, "title"),
                        ["message"] = message,
                        ["type"] = GetString(d, "type"),
                        ["createdAt"] = created.HasValue ? created.Value.ToString("o") : ""
                    });
                }
                return Ok(list.OrderByDescending(x => x["createdAt"]?.ToString() ?? "").Take(30).ToList());
            }
            catch (Exception ex) { _logger.LogError(ex, "Admin notices failed"); return StatusCode(500, new { success = false, message = "Could not load the notices" }); }
        }

        [HttpPost("admin/notices/{id}/remove")]
        public async Task<IActionResult> AdminRemoveNotice(string id)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var noticeRef = _firebase.FirestoreDb.Collection("safety_notices").Document(id);
                var snap = await noticeRef.GetSnapshotAsync();
                if (!snap.Exists) return NotFound(new { success = false, message = "Notice not found." });
                await noticeRef.SetAsync(new Dictionary<string, object>
                {
                    ["active"] = false,
                    ["isActive"] = false,
                    ["removedAt"] = Timestamp.GetCurrentTimestamp()
                }, SetOptions.MergeAll);
                await AuditAsync("remove-notice", id);
                return Ok(new { success = true, message = "Announcement removed." });
            }
            catch (Exception ex) { _logger.LogError(ex, "Remove notice failed {Id}", id); return StatusCode(500, new { success = false, message = "Could not remove it right now" }); }
        }

        // ===== ADMIN: payment (upgrade) requests =====
        [HttpGet("admin/requests")]
        public async Task<IActionResult> AdminRequests([FromQuery] string? status)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var st = string.IsNullOrWhiteSpace(status) ? "new" : status.Trim().ToLowerInvariant();
                var snap = await _firebase.FirestoreDb.Collection("upgrade_requests").WhereEqualTo("status", st).Limit(100).GetSnapshotAsync();
                var list = new List<Dictionary<string, object>>();
                foreach (var doc in snap.Documents)
                {
                    var d = doc.ToDictionary();
                    var created = d.TryGetValue("createdAt", out var c) ? DateOf(c) : null;
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = doc.Id,
                        ["businessId"] = GetString(d, "businessId"),
                        ["businessName"] = GetString(d, "businessName"),
                        ["plan"] = GetString(d, "plan"),
                        ["amount"] = GetString(d, "amount"),
                        ["momoName"] = GetString(d, "momoName"),
                        ["status"] = GetString(d, "status"),
                        ["createdAt"] = created.HasValue ? created.Value.ToString("o") : ""
                    });
                }
                return Ok(list.OrderByDescending(x => x["createdAt"]?.ToString() ?? "").ToList());
            }
            catch (Exception ex) { _logger.LogError(ex, "Admin requests failed"); return StatusCode(500, new { success = false, message = "Could not load requests" }); }
        }

        [HttpPost("admin/requests/{id}/approve")]
        public async Task<IActionResult> AdminApprove(string id, [FromBody] Dictionary<string, object>? body)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var db = _firebase.FirestoreDb;
                var reqRef = db.Collection("upgrade_requests").Document(id);
                var snap = await reqRef.GetSnapshotAsync();
                if (!snap.Exists) return NotFound(new { success = false, message = "Request not found." });
                var r = snap.ToDictionary();
                if (!GetString(r, "status").Equals("new", StringComparison.OrdinalIgnoreCase))
                    return Conflict(new { success = false, message = "This request was already handled." });

                var businessId = GetString(r, "businessId");
                var businessName = GetString(r, "businessName");
                var plan = GetString(r, "plan");
                string done;

                if (plan == "featured")
                {
                    var days = IntOf(body, "days", EnvInt("PLAN_FEATURED_DAYS", 7));
                    var res = await ApplyFeaturedAsync(businessId, days);
                    if (!res.Ok) return BadRequest(new { success = false, message = res.Message });
                    done = res.Message;
                }
                else
                {
                    var items = IntOf(body, "limit", EnvInt("PLAN_LIMIT_ITEMS", 20));
                    var days = IntOf(body, "days", EnvInt("PLAN_LIMIT_DAYS", 30));
                    var res = await ApplyLimitAsync(businessId, items, days);
                    if (!res.Ok) return BadRequest(new { success = false, message = res.Message });
                    done = res.Message;
                }

                await reqRef.SetAsync(new Dictionary<string, object>
                {
                    ["status"] = "approved",
                    ["reviewedAt"] = Timestamp.GetCurrentTimestamp()
                }, SetOptions.MergeAll);
                await AuditAsync("approve", $"{businessName} ({businessId}) - {done}");
                return Ok(new { success = true, message = $"{businessName}: {done}" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Approve failed {Id}", id); return StatusCode(500, new { success = false, message = "Could not approve right now" }); }
        }

        [HttpPost("admin/requests/{id}/reject")]
        public async Task<IActionResult> AdminReject(string id)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var reqRef = _firebase.FirestoreDb.Collection("upgrade_requests").Document(id);
                var snap = await reqRef.GetSnapshotAsync();
                if (!snap.Exists) return NotFound(new { success = false, message = "Request not found." });
                if (!GetString(snap.ToDictionary(), "status").Equals("new", StringComparison.OrdinalIgnoreCase))
                    return Conflict(new { success = false, message = "This request was already handled." });
                await reqRef.SetAsync(new Dictionary<string, object>
                {
                    ["status"] = "rejected",
                    ["reviewedAt"] = Timestamp.GetCurrentTimestamp()
                }, SetOptions.MergeAll);
                await AuditAsync("reject", id);
                return Ok(new { success = true, message = "Request rejected." });
            }
            catch (Exception ex) { _logger.LogError(ex, "Reject failed {Id}", id); return StatusCode(500, new { success = false, message = "Could not reject right now" }); }
        }

        // ===== ADMIN: inbox (support messages + seller reports) =====
        [HttpGet("admin/messages")]
        public async Task<IActionResult> AdminMessages([FromQuery] string? status)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var st = string.Equals(status?.Trim(), "done", StringComparison.OrdinalIgnoreCase) ? "done" : "new";
                var snap = await _firebase.FirestoreDb.Collection("support_messages").WhereEqualTo("status", st).Limit(100).GetSnapshotAsync();
                var list = new List<Dictionary<string, object>>();
                foreach (var doc in snap.Documents)
                {
                    var d = doc.ToDictionary();
                    var created = d.TryGetValue("createdAt", out var c) ? DateOf(c) : null;
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = doc.Id,
                        ["from"] = GetString(d, "buyerName"),
                        ["email"] = GetString(d, "buyerEmail"),
                        ["message"] = GetString(d, "message"),
                        ["status"] = GetString(d, "status"),
                        ["createdAt"] = created.HasValue ? created.Value.ToString("o") : ""
                    });
                }
                return Ok(list.OrderByDescending(x => x["createdAt"]?.ToString() ?? "").ToList());
            }
            catch (Exception ex) { _logger.LogError(ex, "Admin messages failed"); return StatusCode(500, new { success = false, message = "Could not load messages" }); }
        }

        [HttpPost("admin/messages/{id}/done")]
        public async Task<IActionResult> AdminMessageDone(string id)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var msgRef = _firebase.FirestoreDb.Collection("support_messages").Document(id);
                var snap = await msgRef.GetSnapshotAsync();
                if (!snap.Exists) return NotFound(new { success = false, message = "Message not found." });
                await msgRef.SetAsync(new Dictionary<string, object>
                {
                    ["status"] = "done",
                    ["reviewedAt"] = Timestamp.GetCurrentTimestamp()
                }, SetOptions.MergeAll);
                await AuditAsync("message-done", id);
                return Ok(new { success = true, message = "Marked as done." });
            }
            catch (Exception ex) { _logger.LogError(ex, "Message done failed {Id}", id); return StatusCode(500, new { success = false, message = "Could not update it right now" }); }
        }

        [HttpGet("admin/reports")]
        public async Task<IActionResult> AdminReports([FromQuery] string? status)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var st = string.Equals(status?.Trim(), "done", StringComparison.OrdinalIgnoreCase) ? "reviewed" : "new";
                var snap = await _firebase.FirestoreDb.Collection("reports").WhereEqualTo("status", st).Limit(100).GetSnapshotAsync();
                var list = new List<Dictionary<string, object>>();
                foreach (var doc in snap.Documents)
                {
                    var d = doc.ToDictionary();
                    var created = d.TryGetValue("createdAt", out var c) ? DateOf(c) : null;
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = doc.Id,
                        ["businessName"] = GetString(d, "businessName"),
                        ["businessId"] = GetString(d, "businessId"),
                        ["reason"] = GetString(d, "reason"),
                        ["details"] = GetString(d, "details"),
                        ["reporterName"] = GetString(d, "reporterName"),
                        ["reporterPhone"] = GetString(d, "reporterPhone"),
                        ["reporterEmail"] = GetString(d, "reporterEmail"),
                        ["status"] = GetString(d, "status"),
                        ["createdAt"] = created.HasValue ? created.Value.ToString("o") : ""
                    });
                }
                return Ok(list.OrderByDescending(x => x["createdAt"]?.ToString() ?? "").ToList());
            }
            catch (Exception ex) { _logger.LogError(ex, "Admin reports failed"); return StatusCode(500, new { success = false, message = "Could not load reports" }); }
        }

        [HttpPost("admin/reports/{id}/done")]
        public async Task<IActionResult> AdminReportDone(string id)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var repRef = _firebase.FirestoreDb.Collection("reports").Document(id);
                var snap = await repRef.GetSnapshotAsync();
                if (!snap.Exists) return NotFound(new { success = false, message = "Report not found." });
                await repRef.SetAsync(new Dictionary<string, object>
                {
                    ["status"] = "reviewed",
                    ["reviewedAt"] = Timestamp.GetCurrentTimestamp()
                }, SetOptions.MergeAll);
                await AuditAsync("report-reviewed", id);
                return Ok(new { success = true, message = "Marked as reviewed." });
            }
            catch (Exception ex) { _logger.LogError(ex, "Report done failed {Id}", id); return StatusCode(500, new { success = false, message = "Could not update it right now" }); }
        }

        // ===== ADMIN: businesses (limit / top placement) =====
        [HttpGet("admin/businesses")]
        public async Task<IActionResult> AdminBusinesses([FromQuery] string? q)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                q = (q ?? "").Trim();
                if (q.Length < 2) return BadRequest(new { success = false, message = "Type at least 2 letters." });
                var db = _firebase.FirestoreDb;
                var snap = await db.Collection("users").WhereEqualTo("role", "business").Limit(500).GetSnapshotAsync();
                if (snap.Count == 0) snap = await db.Collection("users").Limit(300).GetSnapshotAsync();
                var list = new List<Dictionary<string, object>>();
                foreach (var doc in snap.Documents)
                {
                    var d = doc.ToDictionary();
                    var name = GetString(d, "businessName");
                    if (string.IsNullOrWhiteSpace(name)) name = GetString(d, "fullName");
                    if (string.IsNullOrWhiteSpace(name) || name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    list.Add(new Dictionary<string, object>
                    {
                        ["id"] = doc.Id,
                        ["name"] = name,
                        ["phone"] = GetString(d, "phone"),
                        ["email"] = GetString(d, "email"),
                        ["planText"] = DescribePlan(d)
                    });
                    if (list.Count >= 20) break;
                }
                return Ok(list);
            }
            catch (Exception ex) { _logger.LogError(ex, "Admin business search failed"); return StatusCode(500, new { success = false, message = "Search failed" }); }
        }

        [HttpPost("admin/business/limit")]
        public async Task<IActionResult> AdminGrantLimit([FromBody] Dictionary<string, object> body)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var businessId = GetString(body, "businessId");
                var items = IntOf(body, "limit", EnvInt("PLAN_LIMIT_ITEMS", 20));
                var days = IntOf(body, "days", EnvInt("PLAN_LIMIT_DAYS", 30));
                var res = await ApplyLimitAsync(businessId, items, days);
                if (!res.Ok) return BadRequest(new { success = false, message = res.Message });
                await AuditAsync("grant-limit", $"{businessId} - {res.Message}");
                return Ok(new { success = true, message = res.Message });
            }
            catch (Exception ex) { _logger.LogError(ex, "Grant limit failed"); return StatusCode(500, new { success = false, message = "Could not update the limit" }); }
        }

        [HttpPost("admin/business/featured")]
        public async Task<IActionResult> AdminGrantFeatured([FromBody] Dictionary<string, object> body)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var businessId = GetString(body, "businessId");
                var days = IntOf(body, "days", EnvInt("PLAN_FEATURED_DAYS", 7));
                var res = await ApplyFeaturedAsync(businessId, days);
                if (!res.Ok) return BadRequest(new { success = false, message = res.Message });
                await AuditAsync("grant-featured", $"{businessId} - {res.Message}");
                return Ok(new { success = true, message = res.Message });
            }
            catch (Exception ex) { _logger.LogError(ex, "Grant featured failed"); return StatusCode(500, new { success = false, message = "Could not update top placement" }); }
        }

        [HttpPost("admin/business/reset")]
        public async Task<IActionResult> AdminResetPlan([FromBody] Dictionary<string, object> body)
        {
            var denied = await CheckAdminAsync(); if (denied != null) return denied;
            try
            {
                var businessId = GetString(body, "businessId");
                if (string.IsNullOrWhiteSpace(businessId) || businessId.Length < 10) return BadRequest(new { success = false, message = "Invalid business." });
                var userRef = _firebase.FirestoreDb.Collection("users").Document(businessId);
                var snap = await userRef.GetSnapshotAsync();
                if (!snap.Exists) return NotFound(new { success = false, message = "Business not found." });
                await userRef.UpdateAsync(new Dictionary<string, object>
                {
                    ["publishLimit"] = (long)DashboardService.FreeLimit,
                    ["limitExpiresAt"] = FieldValue.Delete,
                    ["updatedAt"] = Timestamp.GetCurrentTimestamp()
                });
                await AuditAsync("reset-plan", businessId);
                return Ok(new { success = true, message = $"Back to the free plan ({DashboardService.FreeLimit} items)." });
            }
            catch (Exception ex) { _logger.LogError(ex, "Reset plan failed"); return StatusCode(500, new { success = false, message = "Could not reset the plan" }); }
        }

        // Sets the publish limit. Renewing before expiry adds days instead of losing them.
        private async Task<(bool Ok, string Message)> ApplyLimitAsync(string businessId, int items, int days)
        {
            if (string.IsNullOrWhiteSpace(businessId) || businessId.Length < 10) return (false, "Invalid business.");
            if (items <= DashboardService.FreeLimit || items > 500) return (false, $"The limit must be between {DashboardService.FreeLimit + 1} and 500.");
            if (days < 1 || days > 365) return (false, "Days must be between 1 and 365.");
            var userRef = _firebase.FirestoreDb.Collection("users").Document(businessId);
            var snap = await userRef.GetSnapshotAsync();
            if (!snap.Exists) return (false, "Business not found.");
            var d = snap.ToDictionary();
            var start = DateTime.UtcNow;
            if (d.TryGetValue("limitExpiresAt", out var le) && DateOf(le) is DateTime existing && existing > start) start = existing;
            var expiry = start.AddDays(days);
            await userRef.SetAsync(new Dictionary<string, object>
            {
                ["publishLimit"] = (long)items,
                ["limitExpiresAt"] = Ts(expiry),
                ["updatedAt"] = Timestamp.GetCurrentTimestamp()
            }, SetOptions.MergeAll);
            return (true, $"limit {items} items until {expiry:yyyy-MM-dd}");
        }

        // Puts the business's products at the top of the buyer feed
        private async Task<(bool Ok, string Message)> ApplyFeaturedAsync(string businessId, int days)
        {
            if (string.IsNullOrWhiteSpace(businessId) || businessId.Length < 10) return (false, "Invalid business.");
            if (days < 1 || days > 365) return (false, "Days must be between 1 and 365.");
            var db = _firebase.FirestoreDb;
            var userRef = db.Collection("users").Document(businessId);
            var snap = await userRef.GetSnapshotAsync();
            if (!snap.Exists) return (false, "Business not found.");
            var d = snap.ToDictionary();
            var start = DateTime.UtcNow;
            if (d.TryGetValue("featuredUntil", out var fu) && DateOf(fu) is DateTime existing && existing > start) start = existing;
            var until = Ts(start.AddDays(days));
            await userRef.SetAsync(new Dictionary<string, object>
            {
                ["featuredUntil"] = until,
                ["updatedAt"] = Timestamp.GetCurrentTimestamp()
            }, SetOptions.MergeAll);

            var devs = await db.Collection("devices").WhereEqualTo("businessId", businessId).GetSnapshotAsync();
            if (devs.Count == 0) devs = await db.Collection("devices").WhereEqualTo("ownerId", businessId).GetSnapshotAsync();
            var refs = devs.Documents.Select(x => x.Reference).ToList();
            for (int i = 0; i < refs.Count; i += 400)
            {
                var batch = db.StartBatch();
                foreach (var r in refs.Skip(i).Take(400))
                    batch.Set(r, new Dictionary<string, object> { ["featuredUntil"] = until }, SetOptions.MergeAll);
                await batch.CommitAsync();
            }
            return (true, $"top placement for {refs.Count} products until {start.AddDays(days):yyyy-MM-dd}");
        }

        // ===== ADMIN PAGE =====
        [HttpGet("admin-panel")]
        public IActionResult AdminPanel()
        {
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["X-Frame-Options"] = "DENY";
            Response.Headers["Referrer-Policy"] = "no-referrer";
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

            var html = """
<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1, viewport-fit=cover'>
<meta name='robots' content='noindex,nofollow'>
<meta name='theme-color' content='#0A1E4A'>
<title>IrhamTrade Admin</title>
<style>
:root{--navy:#0A1E4A;--navy2:#112B5F;--gold:#D4AF37;--bg:#EEF1F7;--text:#13203F;--muted:#6B7690;--green:#15803D;--red:#DC2626;--line:#E3E7F0}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}
html,body{margin:0}
body{font-family:system-ui,-apple-system,'Segoe UI',Roboto,Arial,sans-serif;background:var(--bg);color:var(--text);font-size:15px}
.hide{display:none!important}
.center{text-align:center}
.pad{padding:24px}
.muted{color:var(--muted);font-size:13px;line-height:1.5}
a{color:var(--navy)}

#gate{min-height:100vh;display:flex;align-items:center;justify-content:center;padding:20px;background:radial-gradient(1100px 520px at 50% -10%,#1E3E8A 0%,var(--navy) 62%)}
.gcard{background:#fff;width:100%;max-width:420px;border-radius:26px;padding:28px 24px;box-shadow:0 26px 70px rgba(0,0,0,.4)}
.logo{width:60px;height:60px;border-radius:19px;background:var(--navy);color:var(--gold);display:flex;align-items:center;justify-content:center;font-weight:800;font-size:21px;margin:0 auto 14px;border:2px solid var(--gold)}
.gcard h1{font-size:22px;margin:0 0 4px;text-align:center;color:var(--navy)}
.gcard .sub{text-align:center;margin:0 0 18px}
label{display:block;font-weight:600;font-size:12.5px;color:#34415F;margin:12px 0 5px}
input,textarea,select{width:100%;padding:13px 14px;border:1.5px solid var(--line);border-radius:12px;font-size:15px;background:#fff;color:var(--text);outline:none;font-family:inherit}
input:focus,textarea:focus,select:focus{border-color:var(--gold)}
.btn{display:inline-block;padding:13px 16px;border:none;border-radius:12px;font-weight:700;font-size:14px;cursor:pointer;font-family:inherit}
.btn.full{width:100%;margin-top:16px}
.gold{background:var(--gold);color:var(--navy)}
.navy{background:var(--navy);color:#fff}
.green{background:var(--green);color:#fff}
.red{background:var(--red);color:#fff}
.ghost{background:#EEF1F7;color:var(--navy)}
.btn.sm{padding:10px 13px;font-size:13px;margin:8px 8px 0 0}
.link{background:none;border:none;color:var(--navy);font-weight:700;font-size:13px;cursor:pointer;padding:12px 0 0;width:100%;text-align:center;font-family:inherit}
.hint{font-size:12px;color:var(--muted);margin-top:6px}

#app{min-height:100vh;padding-bottom:96px}
.top{background:var(--navy);color:#fff;padding:calc(14px + env(safe-area-inset-top)) 18px 16px;display:flex;align-items:center;justify-content:space-between;position:sticky;top:0;z-index:20}
.top b{font-size:17px}
.top .tag{color:var(--gold);font-size:11px;letter-spacing:1.4px;display:block;margin-bottom:2px}
.top button{background:rgba(255,255,255,.12);color:#fff;border:none;border-radius:10px;padding:9px 13px;font-weight:700;font-size:12px;cursor:pointer;font-family:inherit}
#view{max-width:680px;margin:0 auto;padding:18px 14px}
.h{margin:4px 2px 4px;color:var(--navy);font-size:21px}
.card{background:#fff;border-radius:18px;padding:16px;margin-top:12px;box-shadow:0 2px 12px rgba(10,30,74,.07)}
.card h3{margin:0 0 6px;color:var(--navy);font-size:16px}
.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px;margin-top:12px}
.stat{background:#fff;border-radius:16px;padding:14px 8px;text-align:center;box-shadow:0 2px 12px rgba(10,30,74,.07);cursor:pointer}
.stat .n{font-size:26px;font-weight:800;color:var(--navy)}
.stat .l{font-size:11.5px;color:var(--muted);margin-top:2px}
.stat .i{font-size:20px}
.kv{display:flex;justify-content:space-between;gap:10px;padding:8px 0;border-bottom:1px solid var(--line);font-size:14px}
.kv:last-child{border-bottom:none}
.kv span:first-child{color:var(--muted)}
.kv span:last-child{font-weight:700;text-align:right}
.seg{display:flex;background:#fff;border-radius:13px;padding:4px;margin-top:12px;box-shadow:0 2px 12px rgba(10,30,74,.06)}
.seg button{flex:1;border:none;background:none;padding:10px 6px;border-radius:10px;font-weight:700;font-size:13px;color:var(--muted);cursor:pointer;font-family:inherit}
.seg button.on{background:var(--navy);color:#fff}
.item{background:#fff;border-radius:16px;padding:14px;margin-top:10px;box-shadow:0 2px 12px rgba(10,30,74,.07);line-height:1.5}
.item .t{font-weight:800;color:var(--navy);font-size:15.5px}
.item .m{color:var(--muted);font-size:12.5px}
.item .body{margin-top:8px;white-space:pre-wrap;word-break:break-word;font-size:14px}
.pill{display:inline-block;padding:4px 10px;border-radius:20px;font-size:11.5px;font-weight:800;margin-left:6px;vertical-align:middle}
.pill.p1{background:#FFF3CD;color:#8A5A00}.pill.p2{background:#DCFCE7;color:#166534}.pill.p3{background:#FEE2E2;color:#991B1B}.pill.p4{background:#E0E7FF;color:#3730A3}
.empty{text-align:center;color:var(--muted);padding:34px 10px;font-size:14px}
.nav{position:fixed;left:0;right:0;bottom:0;background:#fff;display:flex;border-top:1px solid var(--line);padding-bottom:env(safe-area-inset-bottom);z-index:30}
.nav button{flex:1;background:none;border:none;padding:9px 2px 8px;font-size:10.5px;color:var(--muted);font-weight:700;cursor:pointer;position:relative;font-family:inherit}
.nav button .ic{display:block;font-size:20px;margin-bottom:1px}
.nav button.on{color:var(--navy)}
.nav button.on .ic{transform:translateY(-1px)}
.nav button.on::after{content:'';position:absolute;top:0;left:30%;right:30%;height:3px;border-radius:0 0 4px 4px;background:var(--gold)}
.badge{position:absolute;top:4px;left:calc(50% + 6px);background:var(--red);color:#fff;border-radius:10px;font-size:10px;min-width:17px;padding:1px 4px;font-weight:800}
.toast{position:fixed;left:14px;right:14px;top:calc(12px + env(safe-area-inset-top));max-width:520px;margin:0 auto;padding:13px 14px;border-radius:13px;color:#fff;font-weight:600;font-size:14px;z-index:99;display:none;text-align:center;box-shadow:0 8px 28px rgba(0,0,0,.25)}
.toast.ok{background:var(--green)}.toast.bad{background:var(--red)}
.modal{position:fixed;inset:0;background:rgba(10,30,74,.55);display:none;align-items:flex-end;justify-content:center;z-index:60}
.modal.show{display:flex}
.sheet{background:#fff;width:100%;max-width:520px;border-radius:24px 24px 0 0;padding:22px 20px calc(22px + env(safe-area-inset-bottom))}
.sheet h3{margin:0 0 4px;color:var(--navy);font-size:18px}
.row{display:flex;gap:10px;margin-top:16px}
.row .btn{flex:1}
</style>
</head>
<body>

<div id='gate'>
  <div class='gcard'>
    <div class='logo'>IT</div>
    <div id='gateBody'><p class='muted center'>Loading...</p></div>
  </div>
</div>

<div id='app' class='hide'>
  <div class='top'>
    <div><span class='tag'>IRHAM TRADE</span><b>Admin</b></div>
    <button onclick='logout()'>Log out</button>
  </div>
  <div id='view'></div>
  <div class='nav'>
    <button id='n_home' onclick="go('home')"><span class='ic'>&#127968;</span>Home</button>
    <button id='n_pay' onclick="go('pay')"><span class='ic'>&#128179;</span>Payments<span id='b_pay' class='badge hide'></span></button>
    <button id='n_inbox' onclick="go('inbox')"><span class='ic'>&#128229;</span>Inbox<span id='b_inbox' class='badge hide'></span></button>
    <button id='n_biz' onclick="go('biz')"><span class='ic'>&#127970;</span>Businesses</button>
    <button id='n_notice' onclick="go('notice')"><span class='ic'>&#128226;</span>Notices</button>
  </div>
</div>

<div id='toast' class='toast'></div>
<div id='modal' class='modal'></div>

<script>
const $ = id => document.getElementById(id);
const TK = 'irh_adm_t';
let GATE = { keySet: true, passwordSet: false };
let MODE = 'login';
let TAB = 'home';
let INFO = {};
let SUM = {};
const ST = { req: 'new', itype: 'messages', istatus: 'new' };
const REQS = {}, BIZ = {};

function esc(s){ return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }
function fmt(iso){ if(!iso) return ''; const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleString([], {day:'2-digit', month:'short', year:'numeric', hour:'2-digit', minute:'2-digit'}); }
function toast(t, ok){ const m = $('toast'); m.textContent = t; m.className = 'toast ' + (ok ? 'ok' : 'bad'); m.style.display = 'block'; clearTimeout(window.__tt); window.__tt = setTimeout(() => m.style.display = 'none', 4200); }

async function api(path, method, body, noAuth){
  const ctl = new AbortController();
  const to = setTimeout(() => ctl.abort(), 60000);
  try{
    const h = { 'Content-Type': 'application/json' };
    const t = localStorage.getItem(TK);
    if(t && !noAuth) h['X-Admin-Token'] = t;
    const r = await fetch('/api/Support/' + path, { method: method || 'GET', headers: h, body: body ? JSON.stringify(body) : undefined, signal: ctl.signal });
    const d = await r.json().catch(() => ({}));
    if(d && d.needsSetup){ GATE.passwordSet = false; localStorage.removeItem(TK); showGate(); }
    if(r.status === 401 && !noAuth){ localStorage.removeItem(TK); showGate(); throw new Error('Session ended. Please log in again.'); }
    if(!r.ok || d.success === false) throw new Error(d.message || ('Error ' + r.status));
    return d;
  }catch(e){
    throw new Error(e.name === 'AbortError' ? 'The server is waking up. Wait a minute and try again.' : e.message);
  }finally{ clearTimeout(to); }
}

/* ---------- login gate ---------- */
function pwOk(p){ return p.length >= 10 && p.length <= 100 && /[A-Za-z]/.test(p) && /\d/.test(p); }

function renderGate(){
  const b = $('gateBody');
  if(!GATE.keySet){
    b.innerHTML = `<h1>Admin not ready</h1><p class='muted center'>The admin key is not set on the server. Add <b>ADMIN_KEY</b> (16 or more characters) in the Render settings, redeploy, then reload this page.</p>`;
    return;
  }
  if(!GATE.passwordSet || MODE === 'reset'){
    const first = !GATE.passwordSet;
    b.innerHTML = `<h1>${first ? 'Create admin password' : 'Reset password'}</h1>
      <p class='sub muted'>${first ? 'Enter your admin key once, then choose a strong password.' : 'Enter your admin key and choose a new password.'}</p>
      <label>Admin key</label><input id='g_key' type='password' autocomplete='off' placeholder='Your admin key'>
      <label>New password</label><input id='g_p1' type='password' autocomplete='new-password' placeholder='10+ characters, letters and numbers'>
      <label>Confirm password</label><input id='g_p2' type='password' autocomplete='new-password' placeholder='Type it again'>
      <div class='hint'>At least 10 characters, with letters and numbers.</div>
      <button class='btn gold full' onclick='doSetPassword()'>${first ? 'Create password and enter' : 'Save new password'}</button>
      ${first ? '' : `<button class='link' onclick="setMode('login')">Back to login</button>`}`;
    return;
  }
  b.innerHTML = `<h1>Admin login</h1><p class='sub muted'>Enter your admin password.</p>
    <label>Password</label><input id='g_pw' type='password' autocomplete='current-password' placeholder='Your password'>
    <button class='btn gold full' onclick='doLogin()'>Log in</button>
    <button class='link' onclick="setMode('reset')">Forgot password?</button>`;
  const f = $('g_pw'); if(f) f.addEventListener('keydown', e => { if(e.key === 'Enter') doLogin(); });
}
function setMode(m){ MODE = m; renderGate(); }
function showGate(){ $('app').classList.add('hide'); $('gate').classList.remove('hide'); renderGate(); }

async function doSetPassword(){
  const key = $('g_key').value.trim(), p1 = $('g_p1').value, p2 = $('g_p2').value;
  if(!key){ toast('Enter your admin key', false); return; }
  if(!pwOk(p1)){ toast('Password needs 10+ characters with letters and numbers', false); return; }
  if(p1 !== p2){ toast('The two passwords do not match', false); return; }
  try{
    const r = await api('admin/set-password', 'POST', { key: key, password: p1 }, true);
    localStorage.setItem(TK, r.token); GATE.passwordSet = true; MODE = 'login';
    toast('Password saved. Welcome!', true); showApp();
  }catch(e){ toast(e.message, false); }
}
async function doLogin(){
  const p = $('g_pw').value;
  if(!p){ toast('Enter your password', false); return; }
  try{
    const r = await api('admin/login', 'POST', { password: p }, true);
    localStorage.setItem(TK, r.token); showApp();
  }catch(e){ toast(e.message, false); }
}
function logout(){ localStorage.removeItem(TK); MODE = 'login'; showGate(); }

/* ---------- app shell ---------- */
function showApp(){ $('gate').classList.add('hide'); $('app').classList.remove('hide'); go('home'); }
function go(tab){
  TAB = tab;
  ['home','pay','inbox','biz','notice'].forEach(t => $('n_' + t).classList.toggle('on', t === tab));
  ({ home: renderHome, pay: renderPay, inbox: renderInbox, biz: renderBiz, notice: renderNotices })[tab]();
  window.scrollTo(0, 0);
}
function setBadge(id, n){ const b = $(id); if(!b) return; b.textContent = n > 99 ? '99+' : n; b.classList.toggle('hide', !n); }
async function loadSummary(){
  try{ SUM = await api('admin/summary'); setBadge('b_pay', SUM.newRequests || 0); setBadge('b_inbox', (SUM.newMessages || 0) + (SUM.newReports || 0)); }catch(e){}
}
function seg(key, opts){ return `<div class='seg'>` + opts.map(o => `<button class='${ST[key] === o[0] ? 'on' : ''}' onclick="setSeg('${key}','${o[0]}')">${o[1]}</button>`).join('') + `</div>`; }
function setSeg(key, val){ ST[key] = val; go(TAB); }

/* ---------- modal ---------- */
function ask(title, text, fields, okText, danger){
  return new Promise(res => {
    const m = $('modal');
    m.innerHTML = `<div class='sheet'><h3>${esc(title)}</h3>${text ? `<p class='muted'>${esc(text)}</p>` : ''}` +
      fields.map(f => `<label>${esc(f.label)}</label><input id='mf_${f.id}' type='${f.type || 'text'}' value='${esc(f.value || '')}'>`).join('') +
      `<div class='row'><button class='btn ghost' id='mcancel'>Cancel</button><button class='btn ${danger ? 'red' : 'gold'}' id='mok'>${esc(okText || 'OK')}</button></div></div>`;
    m.classList.add('show');
    $('mcancel').onclick = () => { m.classList.remove('show'); res(null); };
    $('mok').onclick = () => { const v = {}; fields.forEach(f => v[f.id] = $('mf_' + f.id).value); m.classList.remove('show'); res(v); };
  });
}

/* ---------- Home ---------- */
async function renderHome(){
  $('view').innerHTML = `<div class='muted center pad'>Loading...</div>`;
  await loadSummary();
  try{ INFO = await api('plan-info', 'GET', null, true); }catch(e){}
  const s = SUM || {};
  const stat = (i, l, n, act) => `<div class='stat' onclick="${act}"><div class='i'>${i}</div><div class='n'>${n}</div><div class='l'>${l}</div></div>`;
  $('view').innerHTML = `<h2 class='h'>Overview</h2>
    <div class='grid'>${stat('&#128179;', 'Payments', s.newRequests || 0, "go('pay')")}${stat('&#128236;', 'Messages', s.newMessages || 0, "ST.itype='messages';ST.istatus='new';go('inbox')")}${stat('&#128681;', 'Reports', s.newReports || 0, "ST.itype='reports';ST.istatus='new';go('inbox')")}</div>
    <div class='card'><h3>What businesses see</h3>
      <p class='muted'>These come from your Render settings. Change them in Render, no new app needed.</p>
      <div class='kv'><span>MoMo number</span><span>${esc(INFO.momoNumber || 'Not set')}</span></div>
      <div class='kv'><span>MoMo name</span><span>${esc(INFO.momoName || 'Not set')}</span></div>
      <div class='kv'><span>More items</span><span>${esc(INFO.limitItems || '')} items / ${esc(INFO.limitDays || '')} days - GHS ${esc(INFO.limitPrice || 'not set')}</span></div>
      <div class='kv'><span>Top placement</span><span>${esc(INFO.featuredDays || '')} days - GHS ${esc(INFO.featuredPrice || 'not set')}</span></div>
      <div class='kv'><span>Free plan</span><span>${esc(INFO.freeLimit || 10)} items</span></div>
    </div>
    <div class='card'><h3>Security</h3>
      <p class='muted'>Your session lasts 12 hours. Use a screen lock on this phone.</p>
      <button class='btn navy sm' onclick='changePassword()'>Change password</button>
      <button class='btn ghost sm' onclick='logout()'>Log out</button>
    </div>`;
}
async function changePassword(){
  const v = await ask('Change password', 'Enter your admin key and the new password.', [
    { id: 'k', label: 'Admin key', type: 'password' }, { id: 'p1', label: 'New password', type: 'password' }, { id: 'p2', label: 'Confirm new password', type: 'password' }], 'Save');
  if(!v) return;
  if(!pwOk(v.p1)){ toast('Password needs 10+ characters with letters and numbers', false); return; }
  if(v.p1 !== v.p2){ toast('The two passwords do not match', false); return; }
  try{
    const r = await api('admin/set-password', 'POST', { key: v.k.trim(), password: v.p1 }, true);
    localStorage.setItem(TK, r.token); toast('Password changed', true);
  }catch(e){ toast(e.message, false); }
}

/* ---------- Payments ---------- */
function reqCard(r){
  REQS[r.id] = r;
  const pill = r.status === 'approved' ? `<span class='pill p2'>Approved</span>` : r.status === 'rejected' ? `<span class='pill p3'>Rejected</span>` : `<span class='pill p1'>New</span>`;
  const btns = r.status === 'new' ? `<button class='btn green sm' onclick="approveReq('${esc(r.id)}')">Approve</button><button class='btn red sm' onclick="rejectReq('${esc(r.id)}')">Reject</button>` : '';
  return `<div class='item'><div class='t'>${esc(r.businessName)}${pill}</div>
    <div class='m'>${r.plan === 'featured' ? 'Top placement' : 'More items'} &middot; ${esc(fmt(r.createdAt))}</div>
    <div class='kv'><span>Amount sent</span><span>GHS ${esc(r.amount)}</span></div>
    <div class='kv'><span>MoMo name</span><span>${esc(r.momoName)}</span></div>${btns}</div>`;
}
async function renderPay(){
  $('view').innerHTML = `<h2 class='h'>Payment requests</h2><p class='muted'>Find the MoMo name and amount in your Mobile Money messages, then approve.</p>
    ${seg('req', [['new','New'],['approved','Approved'],['rejected','Rejected']])}<div id='list' class='empty'>Loading...</div>`;
  try{
    const d = await api('admin/requests?status=' + ST.req);
    $('list').className = '';
    $('list').innerHTML = d.length ? d.map(reqCard).join('') : `<div class='empty'>Nothing here.</div>`;
    loadSummary();
  }catch(e){ $('list').innerHTML = `<div class='empty'>${esc(e.message)}</div>`; }
}
async function approveReq(id){
  const r = REQS[id]; if(!r) return;
  let fields, body = {};
  if(r.plan === 'featured') fields = [{ id: 'days', label: 'Days at the top', value: INFO.featuredDays || 7, type: 'number' }];
  else fields = [{ id: 'limit', label: 'New item limit', value: INFO.limitItems || 20, type: 'number' }, { id: 'days', label: 'Days it lasts', value: INFO.limitDays || 30, type: 'number' }];
  const v = await ask('Approve for ' + r.businessName, 'GHS ' + r.amount + ' from ' + r.momoName, fields, 'Approve');
  if(!v) return;
  body.days = parseInt(v.days);
  if(v.limit !== undefined) body.limit = parseInt(v.limit);
  try{ const out = await api('admin/requests/' + id + '/approve', 'POST', body); toast(out.message, true); renderPay(); }catch(e){ toast(e.message, false); }
}
async function rejectReq(id){
  const r = REQS[id]; if(!r) return;
  const v = await ask('Reject this request?', r.businessName + ' - GHS ' + r.amount, [], 'Reject', true);
  if(!v) return;
  try{ const out = await api('admin/requests/' + id + '/reject', 'POST', {}); toast(out.message, true); renderPay(); }catch(e){ toast(e.message, false); }
}

/* ---------- Inbox ---------- */
async function renderInbox(){
  $('view').innerHTML = `<h2 class='h'>Inbox</h2>
    ${seg('itype', [['messages','Messages'],['reports','Seller reports']])}
    ${seg('istatus', [['new','New'],['done', ST.itype === 'reports' ? 'Reviewed' : 'Done']])}<div id='list' class='empty'>Loading...</div>`;
  try{
    const d = await api('admin/' + ST.itype + '?status=' + ST.istatus);
    const kind = ST.itype;
    $('list').className = '';
    if(!d.length){ $('list').innerHTML = `<div class='empty'>Nothing here.</div>`; loadSummary(); return; }
    $('list').innerHTML = d.map(x => {
      const act = ST.istatus === 'new' ? `<button class='btn navy sm' onclick="markDone('${kind}','${esc(x.id)}')">Mark ${kind === 'reports' ? 'reviewed' : 'done'}</button>` : '';
      if(kind === 'messages') return `<div class='item'><div class='t'>${esc(x.from || 'Unknown')}</div><div class='m'>${esc(x.email)} &middot; ${esc(fmt(x.createdAt))}</div><div class='body'>${esc(x.message)}</div>${act}</div>`;
      return `<div class='item'><div class='t'>${esc(x.businessName || 'Unknown seller')}<span class='pill p3'>${esc(x.reason)}</span></div>
        <div class='m'>${esc(fmt(x.createdAt))}</div><div class='body'>${esc(x.details)}</div>
        <div class='kv'><span>Reported by</span><span>${esc(x.reporterName)}</span></div>
        <div class='kv'><span>Phone</span><span>${esc(x.reporterPhone)}</span></div>
        <div class='kv'><span>Email</span><span>${esc(x.reporterEmail)}</span></div>
        <div class='kv'><span>Business ID</span><span>${esc(x.businessId)}</span></div>${act}</div>`;
    }).join('');
    loadSummary();
  }catch(e){ $('list').innerHTML = `<div class='empty'>${esc(e.message)}</div>`; }
}
async function markDone(kind, id){
  try{ const r = await api('admin/' + kind + '/' + id + '/done', 'POST', {}); toast(r.message, true); renderInbox(); }catch(e){ toast(e.message, false); }
}

/* ---------- Businesses ---------- */
function renderBiz(){
  $('view').innerHTML = `<h2 class='h'>Businesses</h2><p class='muted'>Search a business to raise its limit or put its products at the top.</p>
    <div class='card'><input id='q' placeholder='Business name'><button class='btn navy full' onclick='findBiz()'>Search</button></div><div id='biz'></div>`;
  const q = $('q'); q.addEventListener('keydown', e => { if(e.key === 'Enter') findBiz(); });
}
async function findBiz(){
  const q = $('q').value.trim();
  if(q.length < 2){ toast('Type at least 2 letters', false); return; }
  $('biz').innerHTML = `<div class='empty'>Searching...</div>`;
  try{
    const d = await api('admin/businesses?q=' + encodeURIComponent(q));
    d.forEach(b => BIZ[b.id] = b);
    $('biz').innerHTML = d.length ? d.map(b => `<div class='item'><div class='t'>${esc(b.name)}</div><div class='m'>${esc(b.phone)} ${esc(b.email)}</div>
      <div class='m'>${esc(b.planText)}</div>
      <button class='btn green sm' onclick="grantLimit('${esc(b.id)}')">Raise limit</button>
      <button class='btn navy sm' onclick="grantTop('${esc(b.id)}')">Top placement</button>
      <button class='btn ghost sm' onclick="resetPlan('${esc(b.id)}')">Back to free</button></div>`).join('') : `<div class='empty'>No business found.</div>`;
  }catch(e){ $('biz').innerHTML = `<div class='empty'>${esc(e.message)}</div>`; }
}
async function grantLimit(id){
  const b = BIZ[id]; if(!b) return;
  const v = await ask('Raise limit - ' + b.name, '', [{ id: 'limit', label: 'New item limit', value: INFO.limitItems || 20, type: 'number' }, { id: 'days', label: 'Days it lasts', value: INFO.limitDays || 30, type: 'number' }], 'Save');
  if(!v) return;
  try{ const r = await api('admin/business/limit', 'POST', { businessId: id, limit: parseInt(v.limit), days: parseInt(v.days) }); toast(r.message, true); findBiz(); }catch(e){ toast(e.message, false); }
}
async function grantTop(id){
  const b = BIZ[id]; if(!b) return;
  const v = await ask('Top placement - ' + b.name, '', [{ id: 'days', label: 'Days at the top', value: INFO.featuredDays || 7, type: 'number' }], 'Save');
  if(!v) return;
  try{ const r = await api('admin/business/featured', 'POST', { businessId: id, days: parseInt(v.days) }); toast(r.message, true); findBiz(); }catch(e){ toast(e.message, false); }
}
async function resetPlan(id){
  const b = BIZ[id]; if(!b) return;
  const v = await ask('Back to free plan?', b.name + ' goes back to the free limit.', [], 'Yes, reset', true);
  if(!v) return;
  try{ const r = await api('admin/business/reset', 'POST', { businessId: id }); toast(r.message, true); findBiz(); }catch(e){ toast(e.message, false); }
}

/* ---------- Notices ---------- */
async function renderNotices(){
  $('view').innerHTML = `<h2 class='h'>Announcements</h2><p class='muted'>Buyers see these in Profile &gt; Help.</p>
    <div class='card'><label>Title</label><input id='n_title' maxlength='100' placeholder='Example: Safety alert'>
      <label>Type</label><select id='n_type'><option value='info'>Info - Blue</option><option value='warning'>Warning - Orange</option><option value='alert'>Alert - Red</option><option value='success'>Success - Green</option></select>
      <label>Message</label><textarea id='n_msg' rows='5' maxlength='2000' placeholder='Dear customers...'></textarea>
      <button class='btn gold full' onclick='publish()'>Publish announcement</button></div>
    <h3 class='h' style='font-size:16px;margin-top:20px'>Live announcements</h3><div id='list' class='empty'>Loading...</div>`;
  loadNotices();
}
async function loadNotices(){
  try{
    const d = await api('admin/notices');
    $('list').className = '';
    $('list').innerHTML = d.length ? d.map(n => `<div class='item'><div class='t'>${esc(n.title || 'Notice')}<span class='pill p4'>${esc(n.type || 'info')}</span></div><div class='m'>${esc(fmt(n.createdAt))}</div><div class='body'>${esc(n.message)}</div>
      <button class='btn red sm' onclick="removeNotice('${esc(n.id)}')">Remove</button></div>`).join('') : `<div class='empty'>No live announcements.</div>`;
  }catch(e){ $('list').innerHTML = `<div class='empty'>${esc(e.message)}</div>`; }
}
async function publish(){
  const title = $('n_title').value.trim(), message = $('n_msg').value.trim(), type = $('n_type').value;
  if(title.length < 3){ toast('Title must be at least 3 characters', false); return; }
  if(message.length < 10){ toast('Message must be at least 10 characters', false); return; }
  try{ await api('announce', 'POST', { title: title, message: message, type: type }); toast('Published. Buyers can see it now.', true); $('n_title').value = ''; $('n_msg').value = ''; loadNotices(); }catch(e){ toast(e.message, false); }
}
async function removeNotice(id){
  const v = await ask('Remove this announcement?', 'Buyers will no longer see it.', [], 'Remove', true);
  if(!v) return;
  try{ const r = await api('admin/notices/' + id + '/remove', 'POST', {}); toast(r.message, true); loadNotices(); }catch(e){ toast(e.message, false); }
}

/* ---------- start ---------- */
async function boot(){
  try{ GATE = await api('admin/status', 'GET', null, true); }
  catch(e){ $('gateBody').innerHTML = `<h1>Can't reach the server</h1><p class='muted center'>${esc(e.message)}</p><button class='btn gold full' onclick='location.reload()'>Try again</button>`; return; }
  if(localStorage.getItem(TK) && GATE.passwordSet){
    try{ await api('admin/summary'); showApp(); return; }catch(e){}
  }
  showGate();
}
boot();
</script>
</body>
</html>
""";
            return Content(html, "text/html");
        }
    }
}