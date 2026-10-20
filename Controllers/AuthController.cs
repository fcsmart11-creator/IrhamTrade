using Microsoft.AspNetCore.Mvc;
using IrhamTrade.Server.Services;

namespace IrhamTrade.Server.Controllers
{
    // Purpose: Auth Controller - Handles register-business, register-buyer, login, forgot-password, reset-password, logout, validate. Returns real FirebaseUid + real name always, like Facebook/TikTok. Never returns fake ID "1".
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

        // Purpose: Status check - verifies auth service running, project irhamtrade-31ca6.
        [HttpGet]
        public IActionResult Status()
        {
            return Ok(new { message = "IrhamTrade Auth service running", project = "irhamtrade-31ca6", ready = true, time = DateTime.UtcNow });
        }

        // Purpose: Register business - validates businessName/email/password, calls AuthService.RegisterBusinessAsync which creates Firebase user + Firestore users/{realId} + SQLite. Returns full profile with real ID, role, businessName, phone, location, token. Never returns "1".
        [HttpPost("register-business")]
        public async Task<IActionResult> RegisterBusiness([FromBody] RegisterBusinessRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.BusinessName))
                    return BadRequest(new { success = false, message = "Please provide business name, email and password" });

                _logger.LogInformation("RegisterBusiness attempt: {Email}", req.Email);
                var result = await _authService.RegisterBusinessAsync(req.BusinessName, req.Phone, req.Location, req.Email, req.Password);

                if (!result.Success)
                {
                    _logger.LogWarning("RegisterBusiness failed {Email}: {Message}", req.Email, result.Message);
                    return BadRequest(new { success = false, message = result.Message });
                }

                return Ok(new
                {
                    success = true,
                    message = result.Message,
                    user_role = result.User!.Role,
                    role = result.User.Role,
                    business_name = result.User.BusinessName,
                    businessName = result.User.BusinessName,
                    fullName = result.User.FullName,
                    full_name = result.User.FullName,
                    name = !string.IsNullOrWhiteSpace(result.User.BusinessName) ? result.User.BusinessName : result.User.FullName,
                    phone = result.User.Phone,
                    location = result.User.Location,
                    user_email = result.User.Email,
                    email = result.User.Email,
                    user_id = result.User.Id,
                    userId = result.User.Id,
                    firebaseUid = result.User.Id,
                    firebase_uid = result.User.Id,
                    id = result.User.Id,
                    idString = result.User.Id,
                    businessId = result.User.Id,
                    ownerId = result.User.Id,
                    user = result.User,
                    token = result.User.Token,
                    idToken = result.User.Token
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RegisterBusiness unexpected error");
                return StatusCode(500, new { success = false, message = "Server is busy at the moment. Please wait a moment and try again" });
            }
        }

        // Purpose: Register buyer - validates fullName/email/password, creates buyer account with real FirebaseUid.
        [HttpPost("register-buyer")]
        public async Task<IActionResult> RegisterBuyer([FromBody] RegisterBuyerRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password) || string.IsNullOrWhiteSpace(req.FullName))
                    return BadRequest(new { success = false, message = "Please provide full name, email and password" });

                _logger.LogInformation("RegisterBuyer attempt: {Email}", req.Email);
                var result = await _authService.RegisterBuyerAsync(req.FullName, req.Phone, req.Location, req.Email, req.Password);

                if (!result.Success)
                {
                    _logger.LogWarning("RegisterBuyer failed {Email}: {Message}", req.Email, result.Message);
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
                    full_name = result.User.FullName,
                    businessName = result.User.BusinessName,
                    business_name = result.User.BusinessName,
                    name = result.User.FullName,
                    phone = result.User.Phone,
                    location = result.User.Location,
                    user_email = result.User.Email,
                    email = result.User.Email,
                    user_id = result.User.Id,
                    userId = result.User.Id,
                    firebaseUid = result.User.Id,
                    firebase_uid = result.User.Id,
                    id = result.User.Id,
                    idString = result.User.Id,
                    buyerId = result.User.Id,
                    user = result.User,
                    token = result.User.Token,
                    idToken = result.User.Token
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RegisterBuyer unexpected error");
                return StatusCode(500, new { success = false, message = "Server is busy at the moment. Please try again shortly" });
            }
        }

        // Purpose: Login - MAIN SECURITY LOGIC like Facebook. Email+password only, returns SAME account with SAME role, SAME real ID, SAME real name, phone, location. No new account created. Validates against SQLite then Firebase fallback to restore real name/ID even if Firestore cleared.
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                    return BadRequest(new { success = false, message = "Please enter email and password" });

                _logger.LogInformation("Login attempt: {Email}", req.Email);
                var result = await _authService.LoginAsync(req.Email, req.Password);

                if (!result.Success || result.User == null)
                {
                    _logger.LogWarning("Login failed {Email}: {Message}", req.Email, result.Message);
                    return Unauthorized(new { success = false, message = result.Message });
                }

                if (result.User.Id == "1" || result.User.Id == "2" || result.User.Id.Length < 10)
                {
                    _logger.LogError("Login returned bad ID {Id} for {Email} - SECURITY BLOCK - Should never happen with fixed AuthService", result.User.Id, req.Email);
                    return StatusCode(500, new { success = false, message = "Server returned invalid ID. Please contact support - fixed server will give real ID." });
                }

                return Ok(new
                {
                    success = true,
                    message = result.Message,
                    is_logged_in = true,
                    user_role = result.User!.Role,
                    role = result.User.Role,
                    business_name = result.User.BusinessName,
                    businessName = result.User.BusinessName,
                    buyer_name = result.User.FullName,
                    fullName = result.User.FullName,
                    full_name = result.User.FullName,
                    name = !string.IsNullOrWhiteSpace(result.User.BusinessName) ? result.User.BusinessName : result.User.FullName,
                    displayName = !string.IsNullOrWhiteSpace(result.User.BusinessName) ? result.User.BusinessName : result.User.FullName,
                    phone = result.User.Phone,
                    location = result.User.Location,
                    user_email = result.User.Email,
                    email = result.User.Email,
                    user_id = result.User.Id,
                    userId = result.User.Id,
                    firebaseUid = result.User.Id,
                    firebase_uid = result.User.Id,
                    id = result.User.Id,
                    idString = result.User.Id,
                    businessId = result.User.Id,
                    ownerId = result.User.Id,
                    buyerId = result.User.Id,
                    user = result.User,
                    token = result.User.Token,
                    idToken = result.User.Token
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Login unexpected error");
                return StatusCode(500, new { success = false, message = "Unable to log in right now. Please check your connection and try again" });
            }
        }

        // Purpose: Forgot password - validates email, calls AuthService.ForgotPasswordAsync. Returns success message. Security: returns success even if email not found to avoid enumeration (like Facebook).
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Email) || !req.Email.Contains("@"))
                    return BadRequest(new { success = false, message = "Please enter a valid email address" });

                _logger.LogInformation("ForgotPassword attempt: {Email}", req.Email);
                var result = await _authService.ForgotPasswordAsync(req.Email);

                if (!result.Success)
                    return BadRequest(new { success = false, message = result.Message });

                return Ok(new { success = true, message = result.Message, email = req.Email.Trim().ToLowerInvariant() });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForgotPassword error");
                return StatusCode(500, new { success = false, message = "Unable to process request. Try again." });
            }
        }

        // Purpose: Reset password - validates email and new password length, updates SQLite and Firebase Auth.
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req)
        {
            try
            {
                if (req == null || string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.NewPassword))
                    return BadRequest(new { success = false, message = "Email and new password required" });
                if (req.NewPassword.Length < 6)
                    return BadRequest(new { success = false, message = "Password must be at least 6 characters" });

                var result = await _authService.ResetPasswordAsync(req.Email, req.NewPassword);
                if (!result.Success)
                    return BadRequest(new { success = false, message = result.Message });

                return Ok(new { success = true, message = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ResetPassword error");
                return StatusCode(500, new { success = false, message = "Unable to reset password. Try again." });
            }
        }

        // Purpose: Logout - invalidates JWT token by blacklisting, allows one phone to switch roles cleanly (business -> buyer). Always returns success like Facebook.
        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            try
            {
                var token = Request.Headers["Authorization"].ToString().Replace("Bearer ", "").Trim();
                if (!string.IsNullOrWhiteSpace(token))
                    await _authService.LogoutAsync(token);

                return Ok(new { success = true, message = "Logged out successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Logout error");
                return Ok(new { success = true, message = "Logged out" });
            }
        }

        // Purpose: Validate token - checks if JWT is blacklisted or expired.
        [HttpGet("validate")]
        public async Task<IActionResult> ValidateToken()
        {
            var token = Request.Headers["Authorization"].ToString().Replace("Bearer ", "").Trim();
            if (string.IsNullOrWhiteSpace(token))
                return Unauthorized(new { success = false, message = "No token" });

            var isValid = await _authService.ValidateTokenAsync(token);
            if (!isValid)
                return Unauthorized(new { success = false, message = "Token invalid or expired" });

            return Ok(new { success = true, message = "Token valid" });
        }
    }

    public class RegisterBusinessRequest { public string BusinessName { get; set; } = ""; public string Phone { get; set; } = ""; public string Location { get; set; } = ""; public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
    public class RegisterBuyerRequest { public string FullName { get; set; } = ""; public string Phone { get; set; } = ""; public string Location { get; set; } = ""; public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
    public class LoginRequest { public string Email { get; set; } = ""; public string Password { get; set; } = ""; }
    public class ForgotPasswordRequest { public string Email { get; set; } = ""; }
    public class ResetPasswordRequest { public string Email { get; set; } = ""; public string NewPassword { get; set; } = ""; }
}