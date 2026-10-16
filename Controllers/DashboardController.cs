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

        // GET api/Devices - LIVE feed for ALL buyers - like Jiji browsing
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            try { var devices = await _svc.GetAllDevicesAsync(); return Ok(devices); }
            catch (Exception ex) { _logger.LogError(ex, "GetAll Devices failed"); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            try { if (string.IsNullOrWhiteSpace(businessId)) return Ok(new List<Dictionary<string, object>>()); return Ok(await _svc.GetDevicesByBusinessAsync(businessId)); }
            catch (Exception ex) { _logger.LogError(ex, "GetByBusiness failed {BusinessId}", businessId); return Ok(new List<Dictionary<string, object>>()); }
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (data == null || data.Count == 0) return BadRequest(new { success = false, message = "Device data is required" });
                var id = await _svc.CreateDeviceAsync(data);
                if (string.IsNullOrEmpty(id)) return StatusCode(500, new { success = false, message = "Unable to create device" });
                return Ok(new { success = true, id, message = "Device created LIVE" });
            }
            catch (Exception ex) { _logger.LogError(ex, "Create Device failed"); return StatusCode(500, new { success = false, message = "Unable to create device" }); }
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
            catch (Exception ex) { _logger.LogError(ex, "Create interest failed"); return StatusCode(500, new { success = false, message = "Unable to submit interest" }); }
        }

        [HttpGet("buyer/{buyerId}")]
        public async Task<IActionResult> GetByBuyer(string buyerId) => Ok(await _svc.GetBuyerInterestsAsync(buyerId));

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId) => Ok(await _svc.GetBusinessInterestsAsync(businessId));

        [HttpPut("{interestId}/status")]
        public async Task<IActionResult> UpdateStatusPut(string interestId, [FromBody] Dictionary<string, string> body)
        {
            if (body == null || !body.TryGetValue("status", out var status)) return BadRequest(new { success = false, message = "Status is required" });
            var ok = await _svc.UpdateInterestStatusAsync(interestId, status);
            return ok ? Ok(new { success = true, message = $"Status updated to {status}" }) : NotFound(new { success = false, message = "Interest not found" });
        }

        [HttpPatch("{orderId}")]
        public async Task<IActionResult> UpdateStatusPatch(string orderId, [FromBody] Dictionary<string, string> body)
        {
            var status = body != null && body.ContainsKey("status") ? body["status"] : "Successful";
            var ok = await _svc.UpdateInterestStatusAsync(orderId, status);
            return ok ? Ok(new { success = true, message = $"Order {orderId} updated to {status}" }) : NotFound(new { success = false, message = "Order not found" });
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