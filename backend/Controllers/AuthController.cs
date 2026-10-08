using Microsoft.AspNetCore.Mvc;
using FactoryQuoteApi.Services;

namespace FactoryQuoteApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly ISecurityService _securityService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(ISecurityService securityService, ILogger<AuthController> logger)
    {
        _securityService = securityService;
        _logger = logger;
    }

    public class LoginRequestDto
    {
        public string Role { get; set; } = string.Empty; // "quote", "admin", "owner"
        public string Password { get; set; } = string.Empty;
    }

    public class ChangePasswordRequestDto
    {
        public string TargetRole { get; set; } = string.Empty; // "quote", "admin"
        public string NewPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return Unauthorized(new { success = false, message = "كلمة المرور مطلوبة" });
        }

        var (success, token, error) = await _securityService.AuthenticateAsync(request.Role, request.Password);
        if (!success || string.IsNullOrEmpty(token))
        {
            return Unauthorized(new { success = false, message = error ?? "كلمة المرور غير صحيحة" });
        }

        return Ok(new
        {
            success = true,
            token,
            role = request.Role.Trim().ToLowerInvariant()
        });
    }

    [HttpGet("verify")]
    public async Task<IActionResult> VerifySession([FromQuery] string role)
    {
        string? token = ExtractBearerToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unauthorized(new { valid = false, message = "غير مصرح" });
        }

        var (isValid, verifiedRole) = await _securityService.ValidateTokenAsync(token, role);
        if (!isValid)
        {
            return Unauthorized(new { valid = false, message = "الجلسة منتهية أو غير صالحة" });
        }

        return Ok(new { valid = true, role = verifiedRole });
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequestDto request)
    {
        // 1. Must be authorized as Owner
        string? token = ExtractBearerToken();
        var (isOwner, _) = await _securityService.ValidateTokenAsync(token, "owner");
        if (!isOwner)
        {
            return Unauthorized(new { success = false, message = "غير مصرح. يجب إدخال كلمة مرور المالك أولاً." });
        }

        // 2. Validate request
        var targetRole = request.TargetRole?.Trim().ToLowerInvariant();
        if (targetRole != "quote" && targetRole != "admin")
        {
            return BadRequest(new { success = false, message = "الجهة المحددة غير صحيحة. اختر عروض الأسعار أو لوحة الإدارة." });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword))
        {
            return BadRequest(new { success = false, message = "يرجى كتابة كلمة المرور الجديدة." });
        }

        if (request.NewPassword.Length < 6)
        {
            return BadRequest(new { success = false, message = "يجب أن تكون كلمة المرور 6 أحرف على الأقل." });
        }

        if (request.NewPassword != request.ConfirmPassword)
        {
            return BadRequest(new { success = false, message = "كلمة المرور وتأكيد كلمة المرور غير متطابقين." });
        }

        // 3. Update password hash securely and invalidate prior sessions
        var (success, error) = await _securityService.ChangePasswordAsync(targetRole, request.NewPassword);
        if (!success)
        {
            return BadRequest(new { success = false, message = error ?? "فشل تحديث كلمة المرور." });
        }

        return Ok(new
        {
            success = true,
            message = targetRole == "quote" 
                ? "تم تحديث كلمة مرور شاشة عروض الأسعار بنجاح وإلغاء الجلسات السابقة." 
                : "تم تحديث كلمة مرور لوحة الإدارة بنجاح وإلغاء الجلسات السابقة."
        });
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        return Ok(new { success = true });
    }

    private string? ExtractBearerToken()
    {
        var authHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader))
            return null;

        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authHeader.Substring(7).Trim();
        }

        return null;
    }
}
