using Microsoft.AspNetCore.Mvc;
using IrhamTrade.Server.Services;

namespace IrhamTrade.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DevicesController : ControllerBase
    {
        private readonly DashboardService _svc;
        public DevicesController(DashboardService svc) => _svc = svc;

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var list = await _svc.GetAllDevicesAsync();
            return Ok(list);
        }

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            return Ok(await _svc.GetDevicesByBusinessAsync(businessId));
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Dictionary<string, object> data)
        {
            var id = await _svc.CreateDeviceAsync(data);
            return Ok(new { id, message = "Device created Boss!" });
        }

        // Boss NEW for Inventory edit Boss - Matches UpdateDeviceAsync Boss
        [HttpPut("{deviceId}")]
        public async Task<IActionResult> Update(string deviceId, [FromBody] Dictionary<string, object> data)
        {
            var ok = await _svc.UpdateDeviceAsync(deviceId, data);
            return ok ? Ok(new { message = "Updated Boss" }) : NotFound();
        }

        [HttpDelete("{deviceId}")]
        public async Task<IActionResult> Delete(string deviceId)
        {
            var ok = await _svc.DeleteDeviceAsync(deviceId);
            return ok ? Ok(new { message = "Deleted Boss" }) : NotFound();
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class InterestsController : ControllerBase
    {
        private readonly DashboardService _svc;
        public InterestsController(DashboardService svc) => _svc = svc;

        // Boss NEW - Supports OrdersPagexaml Boss GET /api/Interests?businessId=xxx
        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] string? businessId, [FromQuery] string? buyerId)
        {
            if (!string.IsNullOrEmpty(businessId))
                return Ok(await _svc.GetBusinessInterestsAsync(businessId));
            if (!string.IsNullOrEmpty(buyerId))
                return Ok(await _svc.GetBuyerInterestsAsync(buyerId));
            return BadRequest("businessId or buyerId required Boss");
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Dictionary<string, object> data)
        {
            var id = await _svc.CreateInterestAsync(data);
            return Ok(new { id, message = "Interest submitted Boss!" });
        }

        [HttpGet("buyer/{buyerId}")]
        public async Task<IActionResult> GetByBuyer(string buyerId)
        {
            return Ok(await _svc.GetBuyerInterestsAsync(buyerId));
        }

        [HttpGet("business/{businessId}")]
        public async Task<IActionResult> GetByBusiness(string businessId)
        {
            return Ok(await _svc.GetBusinessInterestsAsync(businessId));
        }

        [HttpPut("{interestId}/status")]
        public async Task<IActionResult> UpdateStatusPut(string interestId, [FromBody] Dictionary<string, string> body)
        {
            if (!body.TryGetValue("status", out var status)) return BadRequest("status required Boss");
            var ok = await _svc.UpdateInterestStatusAsync(interestId, status);
            return ok ? Ok(new { message = $"Status {status} Boss" }) : NotFound();
        }

        // Boss NEW - Supports OrdersPagexaml Boss PATCH /api/Interests/{orderId}
        [HttpPatch("{orderId}")]
        public async Task<IActionResult> UpdateStatusPatch(string orderId, [FromBody] Dictionary<string, string> body)
        {
            var status = body.ContainsKey("status") ? body["status"] : "Successful";
            var ok = await _svc.UpdateInterestStatusAsync(orderId, status);
            return ok ? Ok(new { message = $"Boss Order {orderId} -> {status} Boss" }) : NotFound();
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class BuyersController : ControllerBase
    {
        private readonly DashboardService _svc;
        public BuyersController(DashboardService svc) => _svc = svc;

        [HttpGet("{buyerId}")]
        public async Task<IActionResult> GetProfile(string buyerId)
        {
            var profile = await _svc.GetBuyerProfileAsync(buyerId);
            return profile == null ? NotFound() : Ok(profile);
        }

        [HttpPut("{buyerId}")]
        public async Task<IActionResult> UpdateProfile(string buyerId, [FromBody] Dictionary<string, object> data)
        {
            var ok = await _svc.UpdateBuyerProfileAsync(buyerId, data);
            return ok ? Ok(new { message = "Profile updated Boss!" }) : BadRequest();
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class BusinessesController : ControllerBase
    {
        private readonly DashboardService _svc;
        public BusinessesController(DashboardService svc) => _svc = svc;

        // Boss NEW - Supports ProfileButton Boss GET /api/Businesses/{businessId}
        [HttpGet("{businessId}")]
        public async Task<IActionResult> GetOne(string businessId)
        {
            var biz = await _svc.GetBusinessAsync(businessId);
            return biz == null ? NotFound() : Ok(biz);
        }

        [HttpGet("all-with-devices")]
        public async Task<IActionResult> GetAllWithDevices()
        {
            var list = await _svc.GetAllBusinessesWithDevicesAsync();
            return Ok(list);
        }
    }
}