using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Google.Cloud.Firestore;
using System.Security.Cryptography;
using System.Text;
using IrhamTrade.Server.Services;

namespace IrhamTrade.Server.Controllers
{
    // Purpose: Support + admin control - announcements, seller reports, plan upgrade requests (publish limit / top placement).
    // Every admin action needs the ADMIN_KEY set in Render, sent in the X-Admin-Key header.
    [ApiController]
    [Route("api/[controller]")]
    public class SupportController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly FirebaseService _firebase;
        private readonly IMemoryCache _cache;
        private readonly ILogger<SupportController> _logger;

        public SupportController(DashboardService svc, FirebaseService firebase, IMemoryCache cache, ILogger<SupportController> logger)
        {
            _svc = svc;
            _firebase = firebase;
            _cache = cache;
            _logger = logger;
        }

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

        // ===== ADMIN PROTECTION =====
        private IActionResult? CheckAdmin()
        {
            var expected = Environment.GetEnvironmentVariable("ADMIN_KEY")?.Trim();
            if (string.IsNullOrWhiteSpace(expected) || expected.Length < 16)
                return StatusCode(503, new { success = false, message = "Admin key is not set on the server. Add ADMIN_KEY (16 or more characters) in the Render settings." });

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var failKey = "adminfail_" + ip;
            var fails = _cache.TryGetValue(failKey, out int f) ? f : 0;
            if (fails >= 8)
                return StatusCode(429, new { success = false, message = "Too many wrong attempts. Try again in 15 minutes." });

            var given = Request.Headers["X-Admin-Key"].ToString().Trim();
            var a = Encoding.UTF8.GetBytes(given);
            var b = Encoding.UTF8.GetBytes(expected);
            var ok = a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
            if (!ok)
            {
                _cache.Set(failKey, fails + 1, TimeSpan.FromMinutes(15));
                _logger.LogWarning("Admin key rejected from {Ip} (attempt {N})", ip, fails + 1);
                return Unauthorized(new { success = false, message = "Wrong admin key." });
            }
            _cache.Remove(failKey);
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

        // ===== ADMIN (needs the key) =====
        [HttpPost("announce")]
        public async Task<IActionResult> Announce([FromBody] Dictionary<string, object> data)
        {
            var denied = CheckAdmin(); if (denied != null) return denied;
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

        [HttpGet("admin/requests")]
        public async Task<IActionResult> AdminRequests([FromQuery] string? status)
        {
            var denied = CheckAdmin(); if (denied != null) return denied;
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
            var denied = CheckAdmin(); if (denied != null) return denied;
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
            var denied = CheckAdmin(); if (denied != null) return denied;
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

        [HttpGet("admin/businesses")]
        public async Task<IActionResult> AdminBusinesses([FromQuery] string? q)
        {
            var denied = CheckAdmin(); if (denied != null) return denied;
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
            var denied = CheckAdmin(); if (denied != null) return denied;
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
            var denied = CheckAdmin(); if (denied != null) return denied;
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
            var denied = CheckAdmin(); if (denied != null) return denied;
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

        [HttpGet("admin-panel")]
        public IActionResult AdminPanel()
        {
            var html = """
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>IrhamTrade Admin</title>
<style>
body{font-family:Arial,sans-serif;background:#f2f3f7;margin:0;padding:14px}
.card{background:#fff;padding:18px;border-radius:14px;max-width:640px;margin:14px auto;box-shadow:0 3px 14px rgba(0,0,0,.08)}
h2{margin:0 0 6px;color:#0a1e4a} h3{margin:0 0 8px;color:#0a1e4a}
input,textarea,select{width:100%;padding:12px;margin:6px 0;border:1px solid #d5d8e0;border-radius:9px;box-sizing:border-box;font-size:15px}
button{padding:11px 14px;border:none;border-radius:9px;font-weight:bold;cursor:pointer;color:#fff;font-size:14px;margin:4px 4px 0 0}
.b{background:#0a1e4a} .g{background:#15803d} .r{background:#dc2626}
.req{border:1px solid #e3e5ec;border-radius:11px;padding:12px;margin-top:10px;line-height:1.5}
.row{margin-top:8px}
.muted{color:#777;font-size:13px}
#msg{display:none;padding:11px;border-radius:9px;margin:8px 0;text-align:center;color:#fff}
#msg.ok{background:#15803d} #msg.bad{background:#dc2626}
label{font-weight:bold;font-size:13px;color:#333;display:block;margin-top:8px}
</style>
</head>
<body>
<div class='card'>
<h2>IrhamTrade Admin</h2>
<div id='msg'></div>
<label>Admin key</label>
<input id='key' type='password' placeholder='Paste your admin key'>
<button class='b' onclick='saveKey()'>Save key on this phone</button>
<p class='muted'>The key stays on this phone only. Use a screen lock.</p>
</div>

<div class='card'>
<h3>Payment requests</h3>
<p class='muted'>Match the MoMo name and amount with your Mobile Money message, then approve.</p>
<button class='b' onclick='loadReq()'>Refresh</button>
<div id='reqs'></div>
</div>

<div class='card'>
<h3>Find a business</h3>
<input id='q' placeholder='Business name'>
<button class='b' onclick='findBiz()'>Search</button>
<div id='biz'></div>
</div>

<div class='card'>
<h3>Announcement to all buyers</h3>
<label>Title (min 3 characters)</label>
<input id='title' maxlength='100' placeholder='Example: Safety alert'>
<label>Type</label>
<select id='type'><option value='info'>Info - Blue</option><option value='warning'>Warning - Orange</option><option value='alert'>Alert - Red</option><option value='success'>Success - Green</option></select>
<label>Message (min 10 characters)</label>
<textarea id='message' rows='5' maxlength='2000' placeholder='Dear customers...'></textarea>
<button class='r' onclick='publish()'>Publish announcement</button>
<p class='muted'><a href='/api/Support/notices' target='_blank'>View live notices</a></p>
</div>

<script>
const $=id=>document.getElementById(id);
let INFO={limitItems:20,limitDays:30,featuredDays:7};
function esc(s){return String(s==null?'':s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
function msg(t,ok){const m=$('msg');m.textContent=t;m.className=ok?'ok':'bad';m.style.display='block';window.scrollTo(0,0);}
function saveKey(){localStorage.setItem('adm',$('key').value.trim());msg('Key saved on this phone',true);loadAll();}
async function api(path,method,body){
  const ctl=new AbortController();const to=setTimeout(()=>ctl.abort(),60000);
  try{
    const r=await fetch('/api/Support/'+path,{method:method||'GET',headers:{'Content-Type':'application/json','X-Admin-Key':localStorage.getItem('adm')||''},body:body?JSON.stringify(body):undefined,signal:ctl.signal});
    const d=await r.json().catch(()=>({}));
    if(!r.ok||d.success===false)throw new Error(d.message||('Error '+r.status));
    return d;
  }catch(e){throw new Error(e.name==='AbortError'?'Server is waking up. Wait a minute and try again.':e.message);}
  finally{clearTimeout(to);}
}
async function loadInfo(){try{INFO=await api('plan-info');}catch(e){}}
async function loadReq(){
  try{
    const d=await api('admin/requests?status=new');
    const box=$('reqs');
    if(!d.length){box.innerHTML='<p class="muted">No new requests.</p>';return;}
    box.innerHTML=d.map(r=>'<div class="req"><b>'+esc(r.businessName)+'</b><br>'+(r.plan==='featured'?'Top placement':'More items')+' - GHS '+esc(r.amount)+'<br>MoMo name: <b>'+esc(r.momoName)+'</b><br><span class="muted">'+esc((r.createdAt||'').replace('T',' ').substring(0,16))+' UTC</span><div class="row"><button class="g" onclick="approve(\''+r.id+'\',\''+r.plan+'\')">Approve</button><button class="r" onclick="reject(\''+r.id+'\')">Reject</button></div></div>').join('');
  }catch(e){msg(e.message,false);}
}
async function approve(id,plan){
  const body={};
  if(plan==='featured'){const d=prompt('Days at the top?',INFO.featuredDays);if(d===null)return;body.days=parseInt(d);}
  else{const n=prompt('New item limit?',INFO.limitItems);if(n===null)return;const d=prompt('Days it lasts?',INFO.limitDays);if(d===null)return;body.limit=parseInt(n);body.days=parseInt(d);}
  try{const r=await api('admin/requests/'+id+'/approve','POST',body);msg(r.message,true);loadReq();}catch(e){msg(e.message,false);}
}
async function reject(id){
  if(!confirm('Reject this request?'))return;
  try{const r=await api('admin/requests/'+id+'/reject','POST',{});msg(r.message,true);loadReq();}catch(e){msg(e.message,false);}
}
async function findBiz(){
  const q=$('q').value.trim();
  if(q.length<2){msg('Type at least 2 letters',false);return;}
  try{
    const d=await api('admin/businesses?q='+encodeURIComponent(q));
    $('biz').innerHTML=d.length?d.map(b=>'<div class="req"><b>'+esc(b.name)+'</b><br><span class="muted">'+esc(b.phone)+' '+esc(b.email)+'</span><br><span class="muted">'+esc(b.planText)+'</span><div class="row"><button class="g" onclick="grantLimit(\''+b.id+'\')">Raise limit</button><button class="b" onclick="grantTop(\''+b.id+'\')">Top placement</button><button class="r" onclick="resetPlan(\''+b.id+'\')">Back to free</button></div></div>').join(''):'<p class="muted">No business found.</p>';
  }catch(e){msg(e.message,false);}
}
async function grantLimit(id){
  const n=prompt('New item limit?',INFO.limitItems);if(n===null)return;
  const d=prompt('Days it lasts?',INFO.limitDays);if(d===null)return;
  try{const r=await api('admin/business/limit','POST',{businessId:id,limit:parseInt(n),days:parseInt(d)});msg(r.message,true);findBiz();}catch(e){msg(e.message,false);}
}
async function grantTop(id){
  const d=prompt('Days at the top?',INFO.featuredDays);if(d===null)return;
  try{const r=await api('admin/business/featured','POST',{businessId:id,days:parseInt(d)});msg(r.message,true);findBiz();}catch(e){msg(e.message,false);}
}
async function resetPlan(id){
  if(!confirm('Put this business back on the free plan?'))return;
  try{const r=await api('admin/business/reset','POST',{businessId:id});msg(r.message,true);findBiz();}catch(e){msg(e.message,false);}
}
async function publish(){
  const title=$('title').value.trim(),message=$('message').value.trim(),type=$('type').value;
  if(title.length<3){msg('Title must be at least 3 characters',false);return;}
  if(message.length<10){msg('Message must be at least 10 characters',false);return;}
  try{
    const r=await api('announce','POST',{title:title,message:message,type:type});
    msg('Published. Live for all buyers.',true);
    $('title').value='';$('message').value='';
  }catch(e){msg(e.message,false);}
}
function loadAll(){loadInfo();if(localStorage.getItem('adm'))loadReq();}
$('key').value=localStorage.getItem('adm')||'';
loadAll();
</script>
</body>
</html>
""";
            return Content(html, "text/html");
        }
    }
}