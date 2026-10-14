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
            _authService = authService;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult Status()
        {
            try
            {
                return Ok(new { message = "IrhamTrade Auth running Boss! Facebook sealed Boss!", project = "irhamtrade-31ca6", ready = true, time = DateTime.UtcNow, sealedVersion = "1000% Boss" });
            }
            catch
            {
                return Ok(new { message = "Auth running Boss!", ready = true });
            }
        }

        [HttpPost("register-business")]
        public async Task<IActionResult> RegisterBusiness([FromBody] RegisterBusinessRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.BusinessName))
                    return BadRequest(new { success = false, message = "Please provide all required fields Boss. Business name, email, password Boss needed Boss." });

                _logger.LogInformation($"RegisterBusiness Boss: {req.Email} - {req.BusinessName}");
                var result = await _authService.RegisterBusinessAsync(req.BusinessName, req.Phone, req.Location, req.Email, req.Password);

                if (!result.Success)
                {
                    _logger.LogWarning($"RegisterBusiness Failed Boss {req.Email}: {result.Message}");
                    return BadRequest(new { success = false, message = result.Message });
                }

                _logger.LogInformation($"RegisterBusiness Success Boss {req.Email}");
                return Ok(new
                {
                    success = true,
                    message = result.Message,
                    user_role = result.User!.Role,
                    role = result.User.Role,
                    business_name = result.User.BusinessName,
                    user_email = result.User.Email,
                    user = result.User,
                    token = result.User.Token
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"RegisterBusiness CRASH Boss {req?.Email}: {ex.Message} - But server still alive Boss");
                return StatusCode(500, new { success = false, message = $"Server busy Boss, try again Boss. Real: {ex.Message} Boss" });
            }
        }

        [HttpPost("register-buyer")]
        public async Task<IActionResult> RegisterBuyer([FromBody] RegisterBuyerRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.FullName))
                    return BadRequest(new { success = false, message = "Please provide all required fields Boss. Full name, email, password Boss needed Boss." });

                _logger.LogInformation($"RegisterBuyer Boss: {req.Email}");
                var result = await _authService.RegisterBuyerAsync(req.FullName, req.Phone, req.Location, req.Email, req.Password);

                if (!result.Success)
                {
                    _logger.LogWarning($"RegisterBuyer Failed Boss {req.Email}: {result.Message}");
                    return BadRequest(new { success = false, message = result.Message });
                }

                return Ok(new
                {
                    success = true,
                    message = result.Message,
                    user_role = result.User!.Role,
                    role = result.User.Role,
                    buyer_name = result.User.FullName,
                    fullName = result.User.FullName,
                    user_email = result.User.Email,
                    user = result.User,
                    token = result.User.Token
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"RegisterBuyer CRASH Boss {req?.Email}: {ex.Message} - Server alive Boss");
                return StatusCode(500, new { success = false, message = $"Server busy Boss, try again Boss. Real: {ex.Message} Boss" });
            }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                    return BadRequest(new { success = false, message = "Please enter email and password Boss." });

                _logger.LogInformation($"Login attempt Boss: {req.Email}");
                var result = await _authService.LoginAsync(req.Email, req.Password);

                if (!result.Success)
                {
                    _logger.LogWarning($"Login Failed Boss {req.Email}: {result.Message}");
                    return Unauthorized(new { success = false, message = result.Message });
                }

                return Ok(new
                {
                    success = true,
                    message = result.Message,
                    is_logged_in = true,
                    user_role = result.User!.Role,
                    role = result.User.Role,
                    business_name = result.User.BusinessName,
                    buyer_name = result.User.FullName,
                    fullName = result.User.FullName,
                    user_email = result.User.Email,
                    user = result.User,
                    token = result.User.Token
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Login CRASH Boss {req?.Email}: {ex.Message} - Server alive Boss");
                return StatusCode(500, new { success = false, message = $"Server busy Boss, try again Boss. Wait 40 seconds Boss if first time Boss. Real: {ex.Message} Boss" });
            }
        }
    }

    public class RegisterBusinessRequest { public string BusinessName { get; set; } = ""; public string Phone { get; set; } = ""; public string Location { get; set; } = ""; public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
    public class RegisterBuyerRequest { public string FullName { get; set; } = ""; public string Phone { get; set; } = ""; public string Location { get; set; } = ""; public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
    public class LoginRequest { public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
}