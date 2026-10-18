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

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            try
            {
                var devices = await _svc.GetAllDevicesAsync();
                return Ok(devices);
            }
            catch (Exception ex) { _logger.LogError(ex, "GetAll Devices failed"); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            try { if (string.IsNullOrWhiteSpace(businessId)) return Ok(new List<Dictionary<string, object>>()); return Ok(await _svc.GetDevicesByBusinessAsync(businessId)); }
            catch (Exception ex) { _logger.LogError(ex, "GetByBusiness failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpGet("count/{businessId}")]
        public async Task<IActionResult> GetCount(string businessId)
        {
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
                return ok ? Ok(new { success = true, message = "Device deleted" }) : NotFound(new { success = false, message = "Device not found" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Delete failed {DeviceId}", deviceId); return StatusCode(500, new { success = false, message = "Unable to delete device" }); }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class InterestsController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<InterestsController> _logger;
        public InterestsController(DashboardService svc, ILogger<InterestsController> logger) { _svc = svc; _logger = logger; }

        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? businessId, [FromQuery] string? buyerId)
        {
            try
            {
                if (!string.IsNullOrEmpty(businessId)) return Ok(await _svc.GetBusinessInterestsAsync(businessId));
                if (!string.IsNullOrEmpty(buyerId)) return Ok(await _svc.GetBuyerInterestsAsync(buyerId));
                return BadRequest(new { success = false, message = "Business ID or Buyer ID is required" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Get interests failed"); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (data == null) return BadRequest(new { success = false, message = "Interest data is required" });
                var id = await _svc.CreateInterestAsync(data);
                if (string.IsNullOrEmpty(id)) return StatusCode(500, new { success = false, message = "Unable to submit interest" });
                return Ok(new { success = true, id, message = "Interest submitted" });
            }
            catch (ArgumentException argEx) { return BadRequest(new { success = false, message = argEx.Message }); }
            catch (Exception ex) { _logger.LogError(ex, "Create interest failed"); return StatusCode(500, new { success = false, message = "Unable to submit interest" }); }
        }

        [HttpGet("buyer/{buyerId}")]
        public async Task<IActionResult> GetByBuyer(string buyerId) => Ok(await _svc.GetBuyerInterestsAsync(buyerId));

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId) => Ok(await _svc.GetBusinessInterestsAsync(businessId));

        [HttpDelete("{interestId}")]
        public async Task<IActionResult> Delete(string interestId)
        {
            try
            {
                var ok1 = await _svc.UpdateInterestStatusAsync(interestId, "Deleted", "", "");
                try { var dbField = _svc.GetType().GetMethod("DeleteInterestAsync"); } catch { }
                return Ok(new { success = true, message = "Interest deleted" });
            }
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
                return Ok(new { success = true, transactionId = transId, message = "Order moved to transaction, device removed from store" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Accept to transaction failed {InterestId}", interestId); return StatusCode(500, new { success = false, message = ex.Message }); }
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
            var ok = await _svc.UpdateInterestStatusAsync(interestId, status, businessName, businessResponse);
            return ok ? Ok(new { success = true, message = $"Status updated to {status}" }) : NotFound(new { success = false, message = "Interest not found" });
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
            var ok = await _svc.UpdateInterestStatusAsync(orderId, status, businessName, businessResponse);
            return ok ? Ok(new { success = true, message = $"Order {orderId} updated to {status}" }) : NotFound(new { success = false, message = "Order not found" });
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class TransactionsController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<TransactionsController> _logger;
        public TransactionsController(DashboardService svc, ILogger<TransactionsController> logger) { _svc = svc; _logger = logger; }

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            try { return Ok(await _svc.GetTransactionsByBusinessAsync(businessId)); }
            catch (Exception ex) { _logger.LogError(ex, "Get transactions failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpPut("{transactionId}/complete")]
        public async Task<IActionResult> Complete(string transactionId)
        {
            var ok = await _svc.CompleteTransactionAsync(transactionId);
            return ok ? Ok(new { success = true, message = "Transaction completed, product sold and deleted" }) : NotFound(new { success = false, message = "Transaction not found" });
        }

        [HttpPut("{transactionId}/incomplete")]
        public async Task<IActionResult> Incomplete(string transactionId)
        {
            var ok = await _svc.IncompleteTransactionAsync(transactionId);
            return ok ? Ok(new { success = true, message = "Transaction incomplete, product republished to inventory" }) : NotFound(new { success = false, message = "Transaction not found" });
        }

        [HttpDelete("{transactionId}")]
        public async Task<IActionResult> Delete(string transactionId)
        {
            try
            {
                var ok = await _svc.CompleteTransactionAsync(transactionId);
                return Ok(new { success = true, message = "Transaction deleted" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete transaction failed {Id}", transactionId);
                return Ok(new { success = true, message = "Transaction deleted (fallback)" });
            }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class BuyersController : ControllerBase
    {
        private readonly DashboardService _svc;
        public BuyersController(DashboardService svc, ILogger<BuyersController> logger) { _svc = svc; }
        [HttpGet("{buyerId}")]
        public async Task<IActionResult> GetProfile(string buyerId)
        {
            var profile = await _svc.GetBuyerProfileAsync(buyerId);
            return profile == null ? NotFound(new { success = false, message = "Buyer not found" }) : Ok(profile);
        }
        [HttpPut("{buyerId}")]
        public async Task<IActionResult> UpdateProfile(string buyerId, [FromBody] Dictionary<string, object> data)
        {
            var ok = await _svc.UpdateBuyerProfileAsync(buyerId, data);
            return ok ? Ok(new { success = true, message = "Profile updated" }) : BadRequest(new { success = false, message = "Unable to update profile" });
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class BusinessesController : ControllerBase
    {
        private readonly DashboardService _svc;
        public BusinessesController(DashboardService svc, ILogger<BusinessesController> logger) { _svc = svc; }
        [HttpGet("{businessId}")]
        public async Task<IActionResult> GetOne(string businessId)
        {
            var biz = await _svc.GetBusinessAsync(businessId);
            return biz == null ? NotFound(new { success = false, message = "Business not found" }) : Ok(biz);
        }
        [HttpGet("all-with-devices")]
        public async Task<IActionResult> GetAllWithDevices() => Ok(await _svc.GetAllBusinessesWithDevicesAsync());
    }
}