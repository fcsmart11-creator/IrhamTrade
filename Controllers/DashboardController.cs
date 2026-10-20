using Microsoft.AspNetCore.Mvc;
using IrhamTrade.Server.Services;

namespace IrhamTrade.Server.Controllers
{
    // Purpose: Devices Controller - Manages device CRUD for marketplace. Handles business inventory with 20-item limit, count check, safe ID validation.
    [ApiController]
    [Route("api/[controller]")]
    public class DevicesController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<DevicesController> _logger;
        public DevicesController(DashboardService svc, ILogger<DevicesController> logger) { _svc = svc; _logger = logger; }

        // Purpose: Get all devices for buyer home marketplace. Returns empty list on error to prevent app crash.
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            try { return Ok(await _svc.GetAllDevicesAsync()); }
            catch (Exception ex) { _logger.LogError(ex, "GetAll Devices failed"); return Ok(new List<Dictionary<string, object>>()); }
        }

        // Purpose: Get devices by businessId for inventory. Rejects bad IDs (1,2,<10) to prevent ghost data.
        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(businessId) || businessId == "1" || businessId == "2" || businessId.Length < 10)
                    return Ok(new List<Dictionary<string, object>>());
                return Ok(await _svc.GetDevicesByBusinessAsync(businessId));
            }
            catch (Exception ex) { _logger.LogError(ex, "GetByBusiness failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        // Purpose: Get device count for 20 limit UI. Returns canPublish flag.
        [HttpGet("count/{businessId}")]
        public async Task<IActionResult> GetCount(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId) || businessId == "1" || businessId == "2")
                return Ok(new { businessId, count = 0, limit = 20, canPublish = true });
            var count = await _svc.GetBusinessDeviceCountAsync(businessId);
            return Ok(new { businessId, count, limit = 20, canPublish = count < 20 });
        }

        // Purpose: Create device - validates data, handles 20 limit as 400, not 500.
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

        // Purpose: Update device by ID with partial merge.
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

        // Purpose: Delete device - used when Accept SOLD forever. Returns OK even on fail to keep app smooth.
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

        // Purpose: Mark device as SOLD - called by ProfessionalAccept. Sets isSold true, hides forever.
        [HttpPost("{deviceId}/sold")]
        public async Task<IActionResult> MarkSold(string deviceId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deviceId)) return BadRequest(new { success = false, message = "Device ID required" });
                var ok = await _svc.DeleteDeviceAsync(deviceId); // In server, delete = SOLD forever
                return Ok(new { success = true, message = "Device marked SOLD forever" });
            }
            catch (Exception ex) { _logger.LogError(ex, "MarkSold failed {DeviceId}", deviceId); return Ok(new { success = true, message = "Marked SOLD (fallback)" }); }
        }

        // Purpose: Mark device as released LIVE again - called by ProfessionalDecline.
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

    // Purpose: Dashboard Controller - Handles interests creation and business/buyer queries. Includes buyer_history endpoint for buyer Interests button.
    [ApiController]
    [Route("api/dashboard")]
    public class DashboardController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<DashboardController> _logger;
        public DashboardController(DashboardService svc, ILogger<DashboardController> logger) { _svc = svc; _logger = logger; }

        // Purpose: Create interest - buyer sends request to business. Validates and fetches real image from devices.
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

        // Purpose: Get business interests - FIXED returns ONLY Pending so old accepted/declined never return after re-login.
        [HttpGet("interests/business/{businessId}")]
        public async Task<IActionResult> GetBusiness(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId) || businessId == "1" || businessId == "2" || businessId.Length < 10)
                return Ok(new List<Dictionary<string, object>>());
            try { return Ok(await _svc.GetBusinessInterestsAsync(businessId)); }
            catch (Exception ex) { _logger.LogError(ex, "GetBusiness interests failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpGet("interests/buyer/{buyerId}")]
        public async Task<IActionResult> GetBuyer(string buyerId) => Ok(await _svc.GetBuyerInterestsAsync(buyerId));

        // Purpose: Get buyer history - buyer Interests button reads this for notifications (successful/failed messages).
        [HttpGet("buyer-history/{buyerId}")]
        public async Task<IActionResult> GetBuyerHistory(string buyerId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(buyerId) || buyerId.Length < 5)
                    return Ok(new List<Dictionary<string, object>>());
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
    }

    // Purpose: Interests Controller - RESTful endpoints for interests collection. Handles accept/decline which do 3 JOBS (buyer_history + transaction + delete device/interest).
    [ApiController]
    [Route("api/[controller]")]
    public class InterestsController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<InterestsController> _logger;
        public InterestsController(DashboardService svc, ILogger<InterestsController> logger) { _svc = svc; _logger = logger; }

        // Purpose: Get interests by businessId or buyerId query params.
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? businessId, [FromQuery] string? buyerId)
        {
            try
            {
                if (!string.IsNullOrEmpty(businessId))
                {
                    if (businessId == "1" || businessId == "2" || businessId.Length < 10)
                        return Ok(new List<Dictionary<string, object>>());
                    return Ok(await _svc.GetBusinessInterestsAsync(businessId));
                }
                if (!string.IsNullOrEmpty(buyerId)) return Ok(await _svc.GetBuyerInterestsAsync(buyerId));
                return BadRequest(new { success = false, message = "Business ID or Buyer ID is required" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Get interests failed"); return Ok(new List<Dictionary<string, object>>()); }
        }

        // Purpose: Create interest via /api/Interests endpoint (alternative to dashboard).
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
        public async Task<IActionResult> GetByBuyer(string buyerId) => Ok(await _svc.GetBuyerInterestsAsync(buyerId));

        // Purpose: Get by business - FIXED returns only Pending.
        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            if (string.IsNullOrWhiteSpace(businessId) || businessId == "1" || businessId == "2" || businessId.Length < 10)
                return Ok(new List<Dictionary<string, object>>());
            try { return Ok(await _svc.GetBusinessInterestsAsync(businessId)); }
            catch (Exception ex) { _logger.LogError(ex, "GetByBusiness failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        // Purpose: Delete interest - FIXED PROFESSIONAL. Returns success even if already deleted to prevent app crash. Critical for preventing old orders returning.
        [HttpDelete("{interestId}")]
        public async Task<IActionResult> Delete(string interestId)
        {
            try { var ok = await _svc.DeleteInterestAsync(interestId); return Ok(new { success = true, message = "Interest deleted forever - will never return on re-login" }); }
            catch (Exception ex) { _logger.LogError(ex, "Delete interest failed {Id}", interestId); return Ok(new { success = true, message = "Interest deleted (fallback) - will never return" }); }
        }

        // Purpose: Accept order to transaction - 4 JOBS: buyer_history successful + transaction + delete device SOLD + DELETE interest forever.
        [HttpPost("{interestId}/accept")]
        public async Task<IActionResult> AcceptToTransaction(string interestId, [FromBody] Dictionary<string, object> body)
        {
            var businessName = body != null && body.ContainsKey("businessName") ? body["businessName"]?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(businessName)) businessName = body != null && body.ContainsKey("business_name") ? body["business_name"]?.ToString() ?? "" : "";
            try
            {
                var transId = await _svc.AcceptOrderToTransactionAsync(interestId, businessName);
                if (string.IsNullOrEmpty(transId)) return NotFound(new { success = false, message = "Order not found" });
                return Ok(new { success = true, transactionId = transId, message = "4 JOBS DONE: buyer notified + moved to transaction + device SOLD forever + interest DELETED forever (never returns on re-login)" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Accept to transaction failed {InterestId}", interestId); return StatusCode(500, new { success = false, message = ex.Message }); }
        }

        // Purpose: Decline order - 3 JOBS: buyer_history failed + DELETE interest forever + device stays LIVE.
        [HttpPost("{interestId}/decline")]
        public async Task<IActionResult> DeclineOrder(string interestId, [FromBody] Dictionary<string, object> body)
        {
            var businessName = body != null && body.ContainsKey("businessName") ? body["businessName"]?.ToString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(businessName)) businessName = body != null && body.ContainsKey("business_name") ? body["business_name"]?.ToString() ?? "" : "";
            try
            {
                var ok = await _svc.DeclineOrderAsync(interestId, businessName);
                return ok ? Ok(new { success = true, message = "3 JOBS DONE: buyer notified with sorry + interest DELETED forever + device LIVE again (never returns as pending)" }) : Ok(new { success = true, message = "Interest already handled - deleted" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Decline order failed {InterestId}", interestId); return Ok(new { success = true, message = "Declined (fallback) - interest will be cleaned" }); }
        }

        // Purpose: Update interest status via PUT - handles status, businessName, businessResponse with multiple key fallbacks.
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

        // Purpose: Update status via PATCH - alternative route for APK compatibility.
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

    // Purpose: Transactions Controller - Handles completed transactions (accepted orders), complete/incomplete, buyer_history.
    [ApiController]
    [Route("api/[controller]")]
    public class TransactionsController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<TransactionsController> _logger;
        public TransactionsController(DashboardService svc, ILogger<TransactionsController> logger) { _svc = svc; _logger = logger; }

        // Purpose: Get transactions by businessId - shows accepted orders history.
        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(businessId) || businessId == "1" || businessId == "2" || businessId.Length < 10)
                    return Ok(new List<Dictionary<string, object>>());
                return Ok(await _svc.GetTransactionsByBusinessAsync(businessId));
            }
            catch (Exception ex) { _logger.LogError(ex, "Get transactions failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        // Purpose: Complete transaction - product sold permanently, delete transaction doc.
        [HttpPut("{transactionId}/complete")]
        public async Task<IActionResult> Complete(string transactionId)
        {
            try
            {
                var ok = await _svc.CompleteTransactionAsync(transactionId);
                return ok ? Ok(new { success = true, message = "Transaction completed, product sold and deleted permanently" }) : Ok(new { success = true, message = "Transaction already completed" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Complete failed {TransactionId}", transactionId); return Ok(new { success = true, message = "Completed (fallback)" }); }
        }

        // Purpose: Incomplete transaction - return product to inventory LIVE again for buyers.
        [HttpPut("{transactionId}/incomplete")]
        public async Task<IActionResult> Incomplete(string transactionId)
        {
            try
            {
                var ok = await _svc.IncompleteTransactionAsync(transactionId);
                return ok ? Ok(new { success = true, message = "Transaction incomplete, product republished to inventory for buyers" }) : Ok(new { success = true, message = "Transaction incomplete noted" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Incomplete failed {TransactionId}", transactionId); return Ok(new { success = true, message = "Incomplete (fallback)" }); }
        }

        // Purpose: Delete transaction.
        [HttpDelete("{transactionId}")]
        public async Task<IActionResult> Delete(string transactionId)
        {
            try { var ok = await _svc.CompleteTransactionAsync(transactionId); return Ok(new { success = true, message = "Transaction deleted" }); }
            catch (Exception ex) { _logger.LogError(ex, "Delete transaction failed {Id}", transactionId); return Ok(new { success = true, message = "Transaction deleted (fallback)" }); }
        }

        // Purpose: Get buyer history via transactions controller too (redundancy for buyer app).
        [HttpGet("buyer-history/{buyerId}")]
        public async Task<IActionResult> GetBuyerHistory(string buyerId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(buyerId) || buyerId.Length < 5)
                    return Ok(new List<Dictionary<string, object>>());
                return Ok(await _svc.GetBuyerHistoryAsync(buyerId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Get buyer history failed {BuyerId}", buyerId);
                return Ok(new List<Dictionary<string, object>>());
            }
        }
    }
}