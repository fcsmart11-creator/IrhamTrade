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
        public DevicesController(DashboardService svc, ILogger<DevicesController> logger)
        {
            _svc = svc;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            try
            {
                var list = await _svc.GetAllDevicesAsync();
                return Ok(list);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetAll Devices FAIL Boss");
                return Ok(new List<Dictionary<string, object>>()); // Facebook - return empty not crash
            }
        }

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(businessId)) return Ok(new List<Dictionary<string, object>>());
                return Ok(await _svc.GetDevicesByBusinessAsync(businessId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetByBusiness FAIL Boss {BusinessId}", businessId);
                return Ok(new List<Dictionary<string, object>>());
            }
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (data == null) return BadRequest(new { message = "Data required Boss" });
                var id = await _svc.CreateDeviceAsync(data);
                if (string.IsNullOrEmpty(id)) return StatusCode(500, new { message = "Create failed Boss - Try again Boss" });
                return Ok(new { id, message = "Device created Boss!" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create Device FAIL Boss: {Message}", ex.Message);
                return StatusCode(500, new { message = $"Create failed Boss: {ex.Message} Boss", error = ex.Message });
            }
        }

        [HttpPut("{deviceId}")]
        public async Task<IActionResult> Update(string deviceId, [FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deviceId) || data == null) return BadRequest(new { message = "deviceId and data required Boss" });
                var ok = await _svc.UpdateDeviceAsync(deviceId, data);
                return ok ? Ok(new { message = "Updated Boss" }) : NotFound(new { message = $"Device {deviceId} not found Boss" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Update Device FAIL Boss {DeviceId}", deviceId);
                return StatusCode(500, new { message = $"Update failed Boss: {ex.Message} Boss" });
            }
        }

        [HttpDelete("{deviceId}")]
        public async Task<IActionResult> Delete(string deviceId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deviceId)) return BadRequest(new { message = "deviceId required Boss" });
                var ok = await _svc.DeleteDeviceAsync(deviceId);
                return ok ? Ok(new { message = "Deleted Boss" }) : NotFound(new { message = $"Device {deviceId} not found Boss" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete Device FAIL Boss {DeviceId}", deviceId);
                return StatusCode(500, new { message = $"Delete failed Boss: {ex.Message} Boss" });
            }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class InterestsController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<InterestsController> _logger;
        public InterestsController(DashboardService svc, ILogger<InterestsController> logger)
        {
            _svc = svc;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? businessId, [FromQuery] string? buyerId)
        {
            try
            {
                if (!string.IsNullOrEmpty(businessId))
                    return Ok(await _svc.GetBusinessInterestsAsync(businessId));
                if (!string.IsNullOrEmpty(buyerId))
                    return Ok(await _svc.GetBuyerInterestsAsync(buyerId));
                return BadRequest(new { message = "businessId or buyerId required Boss" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetAll Interests FAIL Boss");
                return Ok(new List<Dictionary<string, object>>());
            }
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (data == null) return BadRequest(new { message = "Data required Boss" });
                var id = await _svc.CreateInterestAsync(data);
                if (string.IsNullOrEmpty(id)) return StatusCode(500, new { message = "Interest create failed Boss" });
                return Ok(new { id, message = "Interest submitted Boss!" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Create Interest FAIL Boss");
                return StatusCode(500, new { message = $"Interest failed Boss: {ex.Message} Boss" });
            }
        }

        [HttpGet("buyer/{buyerId}")]
        public async Task<IActionResult> GetByBuyer(string buyerId)
        {
            try
            {
                return Ok(await _svc.GetBuyerInterestsAsync(buyerId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetByBuyer FAIL Boss {BuyerId}", buyerId);
                return Ok(new List<Dictionary<string, object>>());
            }
        }

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            try
            {
                return Ok(await _svc.GetBusinessInterestsAsync(businessId));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetByBusiness Interests FAIL Boss {BusinessId}", businessId);
                return Ok(new List<Dictionary<string, object>>());
            }
        }

        [HttpPut("{interestId}/status")]
        public async Task<IActionResult> UpdateStatusPut(string interestId, [FromBody] Dictionary<string, string> body)
        {
            try
            {
                if (body == null || !body.TryGetValue("status", out var status)) return BadRequest(new { message = "status required Boss" });
                var ok = await _svc.UpdateInterestStatusAsync(interestId, status);
                return ok ? Ok(new { message = $"Status {status} Boss" }) : NotFound(new { message = $"Interest {interestId} not found Boss" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateStatusPut FAIL Boss {InterestId}", interestId);
                return StatusCode(500, new { message = $"Update status failed Boss: {ex.Message} Boss" });
            }
        }

        [HttpPatch("{orderId}")]
        public async Task<IActionResult> UpdateStatusPatch(string orderId, [FromBody] Dictionary<string, string> body)
        {
            try
            {
                var status = body != null && body.ContainsKey("status") ? body["status"] : "Successful";
                var ok = await _svc.UpdateInterestStatusAsync(orderId, status);
                return ok ? Ok(new { message = $"Boss Order {orderId} -> {status} Boss" }) : NotFound(new { message = $"Order {orderId} not found Boss" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateStatusPatch FAIL Boss {OrderId}", orderId);
                return StatusCode(500, new { message = $"Patch failed Boss: {ex.Message} Boss" });
            }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class BuyersController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<BuyersController> _logger;
        public BuyersController(DashboardService svc, ILogger<BuyersController> logger)
        {
            _svc = svc;
            _logger = logger;
        }

        [HttpGet("{buyerId}")]
        public async Task<IActionResult> GetProfile(string buyerId)
        {
            try
            {
                var profile = await _svc.GetBuyerProfileAsync(buyerId);
                return profile == null ? NotFound(new { message = $"Buyer {buyerId} not found Boss" }) : Ok(profile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetProfile FAIL Boss {BuyerId}", buyerId);
                return StatusCode(500, new { message = $"Get profile failed Boss: {ex.Message} Boss" });
            }
        }

        [HttpPut("{buyerId}")]
        public async Task<IActionResult> UpdateProfile(string buyerId, [FromBody] Dictionary<string, object> data)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(buyerId) || data == null) return BadRequest(new { message = "buyerId and data required Boss" });
                var ok = await _svc.UpdateBuyerProfileAsync(buyerId, data);
                return ok ? Ok(new { message = "Profile updated Boss!" }) : BadRequest(new { message = "Update failed Boss" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpdateProfile FAIL Boss {BuyerId}", buyerId);
                return StatusCode(500, new { message = $"Update profile failed Boss: {ex.Message} Boss" });
            }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class BusinessesController : ControllerBase
    {
        private readonly DashboardService _svc;
        private readonly ILogger<BusinessesController> _logger;
        public BusinessesController(DashboardService svc, ILogger<BusinessesController> logger)
        {
            _svc = svc;
            _logger = logger;
        }

        [HttpGet("{businessId}")]
        public async Task<IActionResult> GetOne(string businessId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(businessId)) return BadRequest(new { message = "businessId required Boss" });
                var biz = await _svc.GetBusinessAsync(businessId);
                return biz == null ? NotFound(new { message = $"Business {businessId} not found Boss" }) : Ok(biz);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetOne Business FAIL Boss {BusinessId}", businessId);
                return StatusCode(500, new { message = $"Get business failed Boss: {ex.Message} Boss" });
            }
        }

        [HttpGet("all-with-devices")]
        public async Task<IActionResult> GetAllWithDevices()
        {
            try
            {
                var list = await _svc.GetAllBusinessesWithDevicesAsync();
                return Ok(list);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetAllWithDevices FAIL Boss");
                return Ok(new List<Dictionary<string, object>>());
            }
        }
    }
}