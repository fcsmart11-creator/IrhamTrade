using Microsoft.AspNetCore.Mvc;
using IrhamTrade.Server.Services;

namespace IrhamTrade.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AuthService _authService;
        private readonly ILogger<AuthController> _logger;
        public AuthController(AuthService authService, ILogger<AuthController> logger)
        {
            _authService = authService; _logger = logger;
        }

        [HttpGet] public IActionResult Status() => Ok(new { message = "IrhamTrade Auth running Boss!", project = "irhamtrade-31ca6", ready = true });

        [HttpPost("register-business")]
        public async Task<IActionResult> RegisterBusiness([FromBody] RegisterBusinessRequest req)
        {
            var result = await _authService.RegisterBusinessAsync(req.BusinessName, req.Phone, req.Location, req.Email, req.Password);
            if (!result.Success) return BadRequest(new { success = false, message = result.Message });
            return Ok(new { success = true, message = result.Message, user_role = result.User!.Role, role = result.User.Role, business_name = result.User.BusinessName, user_email = result.User.Email, user = result.User, token = result.User.Token });
        }

        [HttpPost("register-buyer")]
        public async Task<IActionResult> RegisterBuyer([FromBody] RegisterBuyerRequest req)
        {
            var result = await _authService.RegisterBuyerAsync(req.FullName, req.Phone, req.Location, req.Email, req.Password);
            if (!result.Success) return BadRequest(new { success = false, message = result.Message });
            return Ok(new { success = true, message = result.Message, user_role = result.User!.Role, role = result.User.Role, buyer_name = result.User.FullName, user_email = result.User.Email, user = result.User, token = result.User.Token });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            var result = await _authService.LoginAsync(req.Email, req.Password);
            if (!result.Success) return Unauthorized(new { success = false, message = result.Message });
            return Ok(new { success = true, message = result.Message, is_logged_in = true, user_role = result.User!.Role, role = result.User.Role, business_name = result.User.BusinessName, buyer_name = result.User.FullName, user_email = result.User.Email, user = result.User, token = result.User.Token });
        }
    }

    public class RegisterBusinessRequest { public string BusinessName { get; set; } = ""; public string Phone { get; set; } = ""; public string Location { get; set; } = ""; public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
    public class RegisterBuyerRequest { public string FullName { get; set; } = ""; public string Phone { get; set; } = ""; public string Location { get; set; } = ""; public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
    public class LoginRequest { public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
}