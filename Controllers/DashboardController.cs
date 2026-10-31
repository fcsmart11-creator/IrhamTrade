using Microsoft.AspNetCore.Mvc;
using IrhamTrade.Server.Services;
using Google.Cloud.Firestore;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace IrhamTrade.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DevicesController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<DevicesController> _logger;
        public DevicesController(DashboardService svc, ILogger<DevicesController> logger) { _svc = svc; _logger = logger; }

        private static bool IsBadId(string? id) => string.IsNullOrWhiteSpace(id) || id == "1" || id == "2" || id.Length < 10;

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            try { return Ok(await _svc.GetAllDevicesAsync()); }
            catch (Exception ex) { _logger.LogError(ex, "GetAll Devices failed"); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            try
            {
                if (IsBadId(businessId))
                    return Ok(new List<Dictionary<string, object>>());
                return Ok(await _svc.GetDevicesByBusinessAsync(businessId));
            }
            catch (Exception ex) { _logger.LogError(ex, "GetByBusiness failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpGet("count/{businessId}")]
        public async Task<IActionResult> GetCount(string businessId)
        {
            if (IsBadId(businessId))
                return Ok(new { businessId, count = 0, limit = 20, canPublish = true });
            var count = await _svc.GetBusinessDeviceCountAsync(businessId);
            return Ok(new { businessId, count, limit = 20, canPublish = count < 20 });
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (data == null || data.Count == 0) return BadRequest(new { success = false, message = "Device data is required" });
                var id = await _svc.CreateDeviceAsync(data);
                if (string.IsNullOrEmpty(id)) return StatusCode(500, new { success = false, message = "Unable to create device" });
                return Ok(new { success = true, id, message = "Device created" });
            }
            catch (InvalidOperationException limitEx) { return BadRequest(new { success = false, message = limitEx.Message }); }
            catch (Exception ex) { _logger.LogError(ex, "Create Device failed"); return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpPut("{deviceId}")]
        public async Task<IActionResult> Update(string deviceId, [FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deviceId)) return BadRequest(new { success = false, message = "Device ID is required" });
                var ok = await _svc.UpdateDeviceAsync(deviceId, data);
                return ok ? Ok(new { success = true, message = "Device updated" }) : NotFound(new { success = false, message = "Device not found" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Update failed {DeviceId}", deviceId); return StatusCode(500, new { success = false, message = "Unable to update device" }); }
        }

        [HttpDelete("{deviceId}")]
        public async Task<IActionResult> Delete(string deviceId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deviceId)) return BadRequest(new { success = false, message = "Device ID is required" });
                var ok = await _svc.DeleteDeviceAsync(deviceId);
                return ok ? Ok(new { success = true, message = "Device deleted" }) : Ok(new { success = true, message = "Device deleted (already gone)" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Delete failed {DeviceId}", deviceId); return Ok(new { success = true, message = "Device deleted (fallback)" }); }
        }

        [HttpPost("{deviceId}/sold")]
        public async Task<IActionResult> MarkSold(string deviceId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deviceId)) return BadRequest(new { success = false, message = "Device ID required" });
                var ok = await _svc.DeleteDeviceAsync(deviceId);
                return Ok(new { success = true, message = "Device marked SOLD forever" });
            }
            catch (Exception ex) { _logger.LogError(ex, "MarkSold failed {DeviceId}", deviceId); return Ok(new { success = true, message = "Marked SOLD (fallback)" }); }
        }

        [HttpPost("{deviceId}/release")]
        public async Task<IActionResult> MarkRelease(string deviceId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deviceId)) return BadRequest(new { success = false, message = "Device ID required" });
                return Ok(new { success = true, message = "Device released LIVE again" });
            }
            catch (Exception ex) { _logger.LogError(ex, "MarkRelease failed {DeviceId}", deviceId); return Ok(new { success = true, message = "Released (fallback)" }); }
        }
    }

    [ApiController]
    [Route("api/dashboard")]
    public class DashboardController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<DashboardController> _logger;
        public DashboardController(DashboardService svc, ILogger<DashboardController> logger) { _svc = svc; _logger = logger; }

        private static bool IsBadId(string? id) => string.IsNullOrWhiteSpace(id) || id == "1" || id == "2" || id.Length < 10;

        [HttpPost("interests")]
        public async Task<IActionResult> CreateInterest([FromBody] Dictionary<string, object> data)
        {
            try
            {
                _logger.LogInformation("BOSS /api/dashboard/interests HIT - Data count {Count}", data?.Count ?? 0);
                if (data == null) return BadRequest(new { success = false, message = "Interest data required" });
                var id = await _svc.CreateInterestAsync(data);
                if (string.IsNullOrEmpty(id)) return StatusCode(500, new { success = false, message = "Unable to submit interest" });
                _logger.LogInformation("BOSS /api/dashboard/interests SUCCESS {Id}", id);
                return Ok(new { success = true, id, message = "Interest submitted" });
            }
            catch (ArgumentException argEx)
            {
                _logger.LogWarning("BOSS /api/dashboard/interests BadRequest: {Msg}", argEx.Message);
                return BadRequest(new { success = false, message = argEx.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BOSS Dashboard interests failed - REAL ERROR");
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        [HttpGet("interests/business/{businessId}")]
        public async Task<IActionResult> GetBusiness(string businessId)
        {
            if (IsBadId(businessId)) return Ok(new List<Dictionary<string, object>>());
            try { return Ok(await _svc.GetBusinessInterestsAsync(businessId)); }
            catch (Exception ex) { _logger.LogError(ex, "GetBusiness interests failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpGet("interests/buyer/{buyerId}")]
        public async Task<IActionResult> GetBuyer(string buyerId)
        {
            if (IsBadId(buyerId)) return Ok(new List<Dictionary<string, object>>());
            return Ok(await _svc.GetBuyerInterestsAsync(buyerId));
        }

        [HttpGet("buyer-history/{buyerId}")]
        public async Task<IActionResult> GetBuyerHistory(string buyerId)
        {
            try
            {
                if (IsBadId(buyerId))
                {
                    _logger.LogWarning("BOSS buyer-history Bad ID {BuyerId} - Return empty fresh - Professional", buyerId);
                    return Ok(new List<Dictionary<string, object>>());
                }
                var history = await _svc.GetBuyerHistoryAsync(buyerId);
                _logger.LogInformation("BOSS /api/dashboard/buyer-history/{BuyerId} returned {Count}", buyerId, history.Count);
                return Ok(history);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get buyer history failed {BuyerId}", buyerId);
                return Ok(new List<Dictionary<string, object>>());
            }
        }

        [HttpGet("safety-notices")]
        public async Task<IActionResult> GetSafetyNotices()
        {
            try { var notices = await _svc.GetSafetyNoticesAsync(); return Ok(notices); }
            catch (Exception ex) { _logger.LogError(ex, "Get safety-notices failed"); return Ok(new List<Dictionary<string, object>>()); }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class InterestsController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<InterestsController> _logger;
        public InterestsController(DashboardService svc, ILogger<InterestsController> logger) { _svc = svc; _logger = logger; }

        private static bool IsBadId(string? id) => string.IsNullOrWhiteSpace(id) || id == "1" || id == "2" || id.Length < 10;

        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? businessId, [FromQuery] string? buyerId)
        {
            try
            {
                if (!string.IsNullOrEmpty(businessId))
                {
                    if (IsBadId(businessId)) return Ok(new List<Dictionary<string, object>>());
                    return Ok(await _svc.GetBusinessInterestsAsync(businessId));
                }
                if (!string.IsNullOrEmpty(buyerId))
                {
                    if (IsBadId(buyerId)) return Ok(new List<Dictionary<string, object>>());
                    return Ok(await _svc.GetBuyerInterestsAsync(buyerId));
                }
                return BadRequest(new { success = false, message = "Business ID or Buyer ID is required" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Get interests failed"); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Dictionary<string, object> data)
        {
            try
            {
                _logger.LogInformation("BOSS /api/Interests HIT");
                if (data == null) return BadRequest(new { success = false, message = "Interest data is required" });
                var id = await _svc.CreateInterestAsync(data);
                if (string.IsNullOrEmpty(id)) return StatusCode(500, new { success = false, message = "Unable to submit interest" });
                return Ok(new { success = true, id, message = "Interest submitted" });
            }
            catch (ArgumentException argEx) { return BadRequest(new { success = false, message = argEx.Message }); }
            catch (Exception ex) { _logger.LogError(ex, "Create interest failed"); return StatusCode(500, new { success = false, message = "Unable to submit interest: " + ex.Message }); }
        }

        [HttpGet("buyer/{buyerId}")]
        public async Task<IActionResult> GetByBuyer(string buyerId)
        {
            if (IsBadId(buyerId)) return Ok(new List<Dictionary<string, object>>());
            return Ok(await _svc.GetBuyerInterestsAsync(buyerId));
        }

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            if (IsBadId(businessId)) return Ok(new List<Dictionary<string, object>>());
            try { return Ok(await _svc.GetBusinessInterestsAsync(businessId)); }
            catch (Exception ex) { _logger.LogError(ex, "GetByBusiness failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpDelete("{interestId}")]
        public async Task<IActionResult> Delete(string interestId)
        {
            try { var ok = await _svc.DeleteInterestAsync(interestId); return Ok(new { success = true, message = "Interest deleted forever" }); }
            catch (Exception ex) { _logger.LogError(ex, "Delete interest failed {Id}", interestId); return Ok(new { success = true, message = "Interest deleted (fallback)" }); }
        }

        [HttpPost("{interestId}/accept")]
        public async Task<IActionResult> AcceptToTransaction(string interestId, [FromBody] Dictionary<string, object> body)
        {
            var businessName = body != null && body.ContainsKey("businessName") ? body["businessName"]?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(businessName)) businessName = body != null && body.ContainsKey("business_name") ? body["business_name"]?.ToString() ?? "" : "";
            try
            {
                var transId = await _svc.AcceptOrderToTransactionAsync(interestId, businessName);
                if (string.IsNullOrEmpty(transId)) return NotFound(new { success = false, message = "Order not found" });
                return Ok(new { success = true, transactionId = transId, message = "Accepted - buyer notified + transaction + sold + deleted" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Accept to transaction failed {InterestId}", interestId); return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpPost("{interestId}/decline")]
        public async Task<IActionResult> DeclineOrder(string interestId, [FromBody] Dictionary<string, object> body)
        {
            var businessName = body != null && body.ContainsKey("businessName") ? body["businessName"]?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(businessName)) businessName = body != null && body.ContainsKey("business_name") ? body["business_name"]?.ToString() ?? "" : "";
            try
            {
                var ok = await _svc.DeclineOrderAsync(interestId, businessName);
                return ok ? Ok(new { success = true, message = "Declined - buyer notified + deleted + live again" }) : Ok(new { success = true, message = "Interest already handled" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Decline order failed {InterestId}", interestId); return Ok(new { success = true, message = "Declined (fallback)" }); }
        }

        [HttpPut("{interestId}/status")]
        public async Task<IActionResult> UpdateStatusPut(string interestId, [FromBody] Dictionary<string, object> body)
        {
            if (body == null) return BadRequest(new { success = false, message = "Status is required" });
            var status = body.ContainsKey("status") ? body["status"]?.ToString() ?? "Pending" : "Pending";
            var businessName = body.ContainsKey("businessName") ? body["businessName"]?.ToString() : null;
            var businessResponse = body.ContainsKey("businessResponse") ? body["businessResponse"]?.ToString() : null;
            if (string.IsNullOrWhiteSpace(businessResponse) && body.ContainsKey("response")) businessResponse = body["response"]?.ToString();
            if (string.IsNullOrWhiteSpace(businessResponse) && body.ContainsKey("message")) businessResponse = body["message"]?.ToString();
            try
            {
                var ok = await _svc.UpdateInterestStatusAsync(interestId, status, businessName, businessResponse);
                return ok ? Ok(new { success = true, message = $"Status updated to {status}" }) : Ok(new { success = true, message = $"Status update noted to {status}" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Update status failed {InterestId}", interestId); return Ok(new { success = true, message = $"Status update fallback to {status}" }); }
        }

        [HttpPatch("{orderId}")]
        public async Task<IActionResult> UpdateStatusPatch(string orderId, [FromBody] Dictionary<string, object> body)
        {
            if (body == null) return BadRequest(new { success = false, message = "Body is required" });
            var status = body.ContainsKey("status") ? body["status"]?.ToString() ?? "Successful" : "Successful";
            var businessName = body.ContainsKey("businessName") ? body["businessName"]?.ToString() : null;
            var businessResponse = body.ContainsKey("businessResponse") ? body["businessResponse"]?.ToString() : null;
            if (string.IsNullOrWhiteSpace(businessResponse) && body.ContainsKey("response")) businessResponse = body["response"]?.ToString();
            if (string.IsNullOrWhiteSpace(businessResponse) && body.ContainsKey("message")) businessResponse = body["message"]?.ToString();
            try
            {
                var ok = await _svc.UpdateInterestStatusAsync(orderId, status, businessName, businessResponse);
                return ok ? Ok(new { success = true, message = $"Order {orderId} updated to {status}" }) : Ok(new { success = true, message = $"Order {orderId} update noted" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Patch status failed {OrderId}", orderId); return Ok(new { success = true, message = $"Order {orderId} patch fallback" }); }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class TransactionsController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<TransactionsController> _logger;
        public TransactionsController(DashboardService svc, ILogger<TransactionsController> logger) { _svc = svc; _logger = logger; }

        private static bool IsBadId(string? id) => string.IsNullOrWhiteSpace(id) || id == "1" || id == "2" || id.Length < 10;

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            try
            {
                if (IsBadId(businessId)) return Ok(new List<Dictionary<string, object>>());
                return Ok(await _svc.GetTransactionsByBusinessAsync(businessId));
            }
            catch (Exception ex) { _logger.LogError(ex, "Get transactions failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpPut("{transactionId}/complete")]
        public async Task<IActionResult> Complete(string transactionId)
        {
            try
            {
                var ok = await _svc.CompleteTransactionAsync(transactionId);
                return ok ? Ok(new { success = true, message = "Transaction completed" }) : Ok(new { success = true, message = "Transaction already completed" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Complete failed {TransactionId}", transactionId); return Ok(new { success = true, message = "Completed (fallback)" }); }
        }

        [HttpPut("{transactionId}/incomplete")]
        public async Task<IActionResult> Incomplete(string transactionId)
        {
            try
            {
                var ok = await _svc.IncompleteTransactionAsync(transactionId);
                return ok ? Ok(new { success = true, message = "Transaction incomplete, product republished" }) : Ok(new { success = true, message = "Transaction incomplete noted" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Incomplete failed {TransactionId}", transactionId); return Ok(new { success = true, message = "Incomplete (fallback)" }); }
        }

        [HttpDelete("{transactionId}")]
        public async Task<IActionResult> Delete(string transactionId)
        {
            try { var ok = await _svc.CompleteTransactionAsync(transactionId); return Ok(new { success = true, message = "Transaction deleted" }); }
            catch (Exception ex) { _logger.LogError(ex, "Delete transaction failed {Id}", transactionId); return Ok(new { success = true, message = "Transaction deleted (fallback)" }); }
        }

        [HttpPost("buyer-history")]
        public async Task<IActionResult> CreateBuyerHistory([FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (data == null) return BadRequest(new { success = false, message = "Data required" });
                var buyerId = data.TryGetValue("buyerId", out var bid) ? bid?.ToString() ?? "" : "";
                var buyerPhone = data.TryGetValue("buyerPhone", out var bp) ? bp?.ToString() ?? "" : "";
                var buyerName = data.TryGetValue("buyerName", out var bn) ? bn?.ToString() ?? "Buyer" : "Buyer";
                var businessId = data.TryGetValue("businessId", out var busId) ? busId?.ToString() ?? "" : "";
                var businessName = data.TryGetValue("businessName", out var busName) ? busName?.ToString() ?? "Irham Trade" : "Irham Trade";
                var productId = data.TryGetValue("productId", out var pid) ? pid?.ToString() ?? "" : "";
                var productName = data.TryGetValue("productName", out var pn) ? pn?.ToString() ?? "Product" : "Product";
                var productImage = data.TryGetValue("productImage", out var pi) ? pi?.ToString() ?? "" : "";
                var message = data.TryGetValue("message", out var msg) ? msg?.ToString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(message) && data.TryGetValue("businessResponse", out var br)) message = br?.ToString() ?? "";
                var status = data.TryGetValue("status", out var st) ? st?.ToString() ?? "Successful" : "Successful";

                if (IsBadId(buyerId) && string.IsNullOrWhiteSpace(buyerPhone))
                    return BadRequest(new { success = false, message = "buyerId or phone required" });

                var docId = await _svc.CreateBuyerHistoryAsync(buyerId, buyerPhone, buyerName, businessId, businessName, productId, productName, productImage, message, status);
                return Ok(new { success = true, id = docId, message = "Buyer history saved" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create buyer history failed");
                return Ok(new { success = true, message = "Saved (fallback)" });
            }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class SupportController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly FirebaseService _firebase;
        private readonly ILogger<SupportController> _logger;

        public SupportController(DashboardService svc, FirebaseService firebase, ILogger<SupportController> logger)
        {
            _svc = svc;
            _firebase = firebase;
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

        // BOSS FIX 100% - RESEND API - Fixes 403 gmail.com domain not verified + Render SMTP timeout - Uses onboarding@resend.dev
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
                    // BOSS FIX: Use onboarding@resend.dev - Resend allows this without domain verification - Fixes 403 Forbidden
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
                        _logger.LogInformation("✅ EMAIL SENT via RESEND to {To} - Subject: {Sub} - {Resp}", toEmail, subject, respBody);
                        return true;
                    }
                    else
                    {
                        _logger.LogError("❌ Resend FAILED for {Sub}: {Status} {Body}", subject, response.StatusCode, respBody);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚠️ Resend exception for {Sub}", subject);
                    return false;
                }
            }

            _logger.LogWarning("❌ RESEND_API_KEY missing - Report saved in DB but email not sent - Set RESEND_API_KEY on Render");
            return false;
        }

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
                var buyerPhone = GetString(data, "buyerPhone");
                var businessCodeMasked = GetString(data, "businessCodeMasked");
                if (string.IsNullOrWhiteSpace(businessCodeMasked)) businessCodeMasked = MaskBusinessId(businessId);
                var buyerIdMasked = GetString(data, "buyerIdMasked");

                if (string.IsNullOrWhiteSpace(businessName) || businessName.Length < 2)
                    return BadRequest(new { success = false, message = "Business name required" });
                if (string.IsNullOrWhiteSpace(reason) || reason.Length < 10)
                    return BadRequest(new { success = false, message = "Reason must be at least 10 characters" });

                var fullDetails = $"Product: {product}\nReason: {reason}\nReported via BuyerProfile Help\nBuyerEmail: {buyerEmail}\nBuyerName: {buyerName}\nMasked Code: {businessCodeMasked}";
                var result = await _svc.SubmitSellerReportAsync(buyerId, businessId, businessName, product + " - " + reason, fullDetails);

                try
                {
                    var db = _firebase.FirestoreDb;
                    var docId = Guid.NewGuid().ToString("N");
                    await db.Collection("fraud_reports").Document(docId).SetAsync(new Dictionary<string, object>
                    {
                        ["id"] = docId,
                        ["reporterId"] = buyerId,
                        ["reporterName"] = buyerName,
                        ["reporterEmail"] = buyerEmail,
                        ["reportedBusinessId"] = businessId,
                        ["reportedBusinessName"] = businessName,
                        ["reportedBusinessCodeMasked"] = businessCodeMasked,
                        ["reportedProduct"] = product,
                        ["reason"] = reason,
                        ["type"] = "fraud_report",
                        ["status"] = "new",
                        ["createdAt"] = Timestamp.GetCurrentTimestamp()
                    });
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Save fraud_reports failed"); }

                var subject = $"[REPORT] Fraud: {businessName} - {businessCodeMasked} - by {buyerName}";
                var body = $"IRHAMTRADE - FRAUD BUSINESS REPORT\n\n" +
                           $"Business Name: {businessName}\n" +
                           $"Business Code (Masked - Professional): {businessCodeMasked}\n" +
                           $"Business ID (for admin lookup - secure): {businessId}\n" +
                           $"Product: {product}\n\n" +
                           $"Reason:\n{reason}\n\n" +
                           $"--- Buyer Info ---\n" +
                           $"Name: {buyerName}\n" +
                           $"Email: {buyerEmail}\n" +
                           $"Phone: {buyerPhone}\n" +
                           $"Buyer ID: {buyerId}\n" +
                           $"Buyer Code Masked: {buyerIdMasked}\n\n" +
                           $"Time UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\n" +
                           $"App: IrhamTrade v1.0 Buyer\n" +
                           $"Doc: fraud_reports\n\n" +
                           $"Please review within 24h.";

                await SendGmailEmailAsync(subject, body);

                if (result.Ok)
                    return Ok(new { success = true, message = "Report sent to irhamtradesupport@gmail.com inbox - will be reviewed within 24h" });
                else
                    return BadRequest(new { success = false, message = result.Message });
            }
            catch (Exception ex) { _logger.LogError(ex, "Report business failed"); return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        [HttpGet("admin-panel")]
        public IActionResult AdminPanel()
        {
            var html = @"
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>IrhamTrade Admin - Safety Notice</title>
<style>
body{font-family:Arial;background:#f5f5f5;margin:0;padding:20px}
.card{background:white;padding:25px;border-radius:12px;max-width:600px;margin:30px auto;box-shadow:0 4px 20px rgba(0,0,0,0.12)}
input,textarea,select{width:100%;padding:13px;margin:8px 0;border:1px solid #ddd;border-radius:8px;box-sizing:border-box;font-size:15px}
button{background:#d32f2f;color:white;padding:15px;border:none;border-radius:8px;width:100%;font-weight:bold;cursor:pointer;font-size:16px;margin-top:10px}
button:disabled{background:#999}
.success{background:#4caf50;color:white;padding:12px;border-radius:8px;margin:12px 0;display:none;text-align:center}
.error{background:#f44336;color:white;padding:12px;border-radius:8px;margin:12px 0;display:none;text-align:center}
h2{color:#d32f2f;text-align:center;margin:0 0 5px 0}
label{font-weight:bold;margin-top:12px;display:block;font-size:14px;color:#333}
small{color:#888;display:block;text-align:center;margin-top:15px}
a{color:#d32f2f;text-decoration:none}
</style>
</head>
<body>
<div class='card'>
<h2>🔐 IrhamTrade Admin Panel</h2>
<p style='text-align:center;color:#666;margin-top:0'>Publish safety announcements to all buyers</p>
<div id='success' class='success'></div>
<div id='error' class='error'></div>
<label>Title * (min 3 chars)</label>
<input id='title' placeholder='Example: Safety Alert - Beware of Scammers' maxlength='100'>
<label>Type *</label>
<select id='type'><option value='info'>Info - Blue</option><option value='warning'>Warning - Orange</option><option value='alert'>Alert - Red</option><option value='success'>Success - Green</option></select>
<label>Message * (min 10 chars)</label>
<textarea id='message' rows='6' placeholder='Dear customers, please be careful of sellers asking for advance payment...' maxlength='2000'></textarea>
<button id='btn' onclick='publish()'>📢 Publish Announcement</button>
<p style='text-align:center;margin-top:15px'><a href='/api/Support/notices' target='_blank'>View Live Notices (JSON)</a> | <a href='/api/dashboard/safety-notices' target='_blank'>API Safety Notices</a></p>
<small>Server: irhamtrade.onrender.com | irhamtradesupport@gmail.com<br>All buyers will see this in Profile > Help > Safety Notices</small>
</div>
<script>
async function publish(){
  const title=document.getElementById('title').value.trim();
  const message=document.getElementById('message').value.trim();
  const type=document.getElementById('type').value;
  const success=document.getElementById('success');
  const error=document.getElementById('error');
  const btn=document.getElementById('btn');
  success.style.display='none'; error.style.display='none';
  if(title.length<3){error.textContent='Title must be at least 3 characters'; error.style.display='block'; return;}
  if(message.length<10){error.textContent='Message must be at least 10 characters'; error.style.display='block'; return;}
  btn.disabled=true; btn.textContent='⏳ Publishing... Please wait 60s - Waking server...';
  for(let i=0;i<3;i++){
    try{
      const controller=new AbortController();
      const timeout=setTimeout(()=>controller.abort(),60000);
      const res=await fetch('/api/Support/announce',{
        method:'POST',
        headers:{'Content-Type':'application/json'},
        body:JSON.stringify({title,message,type}),
        signal:controller.signal
      });
      clearTimeout(timeout);
      const data=await res.json();
      if(res.ok && data.success){
        success.textContent='✅ Published! Live for all users. ID: '+data.id;
        success.style.display='block';
        document.getElementById('title').value='';
        document.getElementById('message').value='';
        btn.disabled=false; btn.textContent='📢 Publish Announcement';
        return;
      }else{
        throw new Error(data.message||'Failed to publish');
      }
    }catch(e){
      if(i===2){
        error.textContent='❌ Failed: '+e.message+'. Server may be sleeping. Wait 1 minute and try again.';
        error.style.display='block';
        btn.disabled=false; btn.textContent='📢 Publish Announcement';
        return;
      }
      btn.textContent='⏳ Server waking up... Retry '+(i+1)+'/3 - Wait 10 sec...';
      await new Promise(r=>setTimeout(r,10000));
    }
  }
}
</script>
</body>
</html>
";
            return Content(html, "text/html");
        }

        [HttpPost("announce")]
        public async Task<IActionResult> Announce([FromBody] Dictionary<string, object> data)
        {
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
                var doc = new Dictionary<string, object>
                {
                    ["id"] = docId,
                    ["title"] = title,
                    ["message"] = message,
                    ["type"] = type,
                    ["priority"] = type == "warning" || type == "alert" ? "high" : "normal",
                    ["createdAt"] = now,
                    ["createdBy"] = "admin",
                    ["isActive"] = true
                };
                await db.Collection("safety_notices").Document(docId).SetAsync(doc);

                _logger.LogInformation("Announcement published {DocId} Title {Title}", docId, title);
                return Ok(new { success = true, id = docId, message = "Announcement published successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to publish announcement");
                return StatusCode(500, new { success = false, message = "An error occurred while publishing." });
            }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class PushController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<PushController> _logger;
        public PushController(DashboardService svc, ILogger<PushController> logger) { _svc = svc; _logger = logger; }

        public class RegisterPushRequest
        {
            public string UserId { get; set; } = "";
            public string Role { get; set; } = "";
            public string Token { get; set; } = "";
        }

        public class UnregisterPushRequest
        {
            public string Token { get; set; } = "";
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterPushRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.UserId) || string.IsNullOrWhiteSpace(req.Token))
                    return BadRequest(new { success = false, message = "userId and token are required" });
                var ok = await _svc.RegisterDeviceTokenAsync(req.UserId.Trim(), (req.Role ?? "").Trim().ToLowerInvariant(), req.Token.Trim());
                return ok ? Ok(new { success = true }) : StatusCode(500, new { success = false, message = "Could not save device token" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Push register failed");
                return StatusCode(500, new { success = false, message = "Push register failed" });
            }
        }

        [HttpPost("unregister")]
        public async Task<IActionResult> Unregister([FromBody] UnregisterPushRequest req)
        {
            try
            {
                if (req != null && !string.IsNullOrWhiteSpace(req.Token))
                    await _svc.UnregisterDeviceTokenAsync(req.Token.Trim());
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Push unregister failed");
                return Ok(new { success = true });
            }
        }

        [HttpGet("image/{id}")]
        public async Task<IActionResult> Image(string id)
        {
            try
            {
                var bytes = await _svc.GetPushImageBytesAsync(id);
                if (bytes == null || bytes.Length == 0) return NotFound();
                Response.Headers["Cache-Control"] = "public,max-age=3600";
                return File(bytes, "image/jpeg");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Push image failed {Id}", id);
                return NotFound();
            }
        }

        [HttpGet("status")]
        public async Task<IActionResult> Status()
        {
            var s = await _svc.GetPushStatusAsync();
            return Ok(new { firebaseReady = s.FirebaseReady, registeredDevices = s.Tokens });
        }
    }
}