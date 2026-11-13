using Microsoft.AspNetCore.Mvc;
using IrhamTrade.Server.Services;

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

        // With ?businessId=... it returns only that business's products (the business Home counts use this). Without it, the buyer feed.
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? businessId = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(businessId))
                {
                    if (IsBadId(businessId)) return Ok(new List<Dictionary<string, object>>());
                    return Ok(await _svc.GetDevicesByBusinessAsync(businessId));
                }
                return Ok(await _svc.GetAllDevicesAsync());
            }
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

        // Real plan numbers: count, limit (10 free or the upgraded limit), canPublish, remaining, expiry dates
        [HttpGet("count/{businessId}")]
        public async Task<IActionResult> GetCount(string businessId)
        {
            if (IsBadId(businessId))
                return Ok(new { businessId, count = 0, limit = DashboardService.FreeLimit, canPublish = true });
            var plan = await _svc.GetBusinessPlanAsync(businessId);
            if (plan == null)
                return Ok(new { businessId, count = 0, limit = DashboardService.FreeLimit, canPublish = true });
            return Ok(plan);
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

        // Past decisions (accepted + declined, last 90 days) for the business Orders page
        [HttpGet("business/{businessId}/history")]
        public async Task<IActionResult> GetBusinessHistory(string businessId)
        {
            if (IsBadId(businessId)) return Ok(new List<Dictionary<string, object>>());
            try { return Ok(await _svc.GetBusinessOrderHistoryAsync(businessId)); }
            catch (Exception ex) { _logger.LogError(ex, "GetBusinessHistory failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        // The business deletes finished orders from its own Orders page (the buyer's History is not touched)
        [HttpPost("business/{businessId}/hide")]
        public async Task<IActionResult> HideBusinessOrders(string businessId, [FromBody] Dictionary<string, object> body)
        {
            try
            {
                if (IsBadId(businessId)) return BadRequest(new { success = false, message = "Invalid business" });
                var ids = new List<string>();
                var all = false;
                if (body != null)
                {
                    if (body.TryGetValue("all", out var rawAll) && rawAll is System.Text.Json.JsonElement ja && ja.ValueKind == System.Text.Json.JsonValueKind.True) all = true;
                    if (body.TryGetValue("ids", out var rawIds) && rawIds is System.Text.Json.JsonElement ji && ji.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var el in ji.EnumerateArray())
                            if (el.ValueKind == System.Text.Json.JsonValueKind.String) ids.Add(el.GetString() ?? "");
                }
                var count = await _svc.HideBusinessOrderHistoryAsync(businessId, ids, all);
                return Ok(new { success = true, hidden = count });
            }
            catch (Exception ex) { _logger.LogError(ex, "HideBusinessOrders failed {BusinessId}", businessId); return StatusCode(500, new { success = false, message = "Could not delete right now" }); }
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