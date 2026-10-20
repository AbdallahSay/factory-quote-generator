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
        public string? Role { get; set; } // "quote", "admin", "owner", or "employee"
        public string? Email { get; set; }
        public string Password { get; set; } = string.Empty;
    }

    public class ChangePasswordRequestDto
    {
        public string TargetRole { get; set; } = string.Empty; // "admin"
        public string NewPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public class ProvisionOwnerRequestDto
    {
        public string BootstrapToken { get; set; } = string.Empty;
        public string OwnerPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    public class RecoverOwnerRequestDto
    {
        public string RecoveryKey { get; set; } = string.Empty;
        public string NewOwnerPassword { get; set; } = string.Empty;
        public string ConfirmPassword { get; set; } = string.Empty;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Password))
        {
            return Unauthorized(new { success = false, message = "كلمة المرور مطلوبة" });
        }

        // 1. Employee login via Email + Password (F07 Official Accounts)
        if (!string.IsNullOrWhiteSpace(request.Email))
        {
            var (empSuccess, empToken, empUser, empError) = await _securityService.AuthenticateEmployeeAsync(request.Email, request.Password);
            if (!empSuccess || string.IsNullOrEmpty(empToken) || empUser == null)
            {
                return Unauthorized(new { success = false, message = empError ?? "البريد الإلكتروني أو كلمة المرور غير صحيحة" });
            }

            return Ok(new
            {
                success = true,
                token = empToken,
                role = "employee",
                user = new
                {
                    id = empUser.Id,
                    fullName = empUser.FullName,
                    email = empUser.Email
                }
            });
        }

        // 2. Reject legacy shared 'quote' password login (F07)
        var requestedRole = request.Role?.Trim().ToLowerInvariant();
        if (requestedRole == "quote")
        {
            return Unauthorized(new 
            { 
                success = false, 
                message = "تم إلغاء كلمة المرور المشتركة لعروض الأسعار نهائياً. يرجى تسجيل الدخول باستخدام البريد الإلكتروني وكلمة المرور الخاصة بحساب الموظف المعتمد." 
            });
        }

        if (string.IsNullOrWhiteSpace(requestedRole))
        {
            return Unauthorized(new { success = false, message = "يرجى إدخال البريد الإلكتروني للموظف أو تحديد الصلاحية الإدارية المطلوبة." });
        }

        // 3. System role login (admin, owner ONLY)
        var (success, token, error) = await _securityService.AuthenticateAsync(requestedRole, request.Password);
        if (!success || string.IsNullOrEmpty(token))
        {
            return Unauthorized(new { success = false, message = error ?? "كلمة المرور غير صحيحة" });
        }

        return Ok(new
        {
            success = true,
            token,
            role = requestedRole
        });
    }

    [HttpGet("verify")]
    public async Task<IActionResult> VerifySession([FromQuery] string? role)
    {
        string? token = ExtractBearerToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Unauthorized(new { valid = false, message = "غير مصرح" });
        }

        var (isValid, user, verifiedRole) = await _securityService.ValidateUserTokenAsync(token);
        if (!isValid || verifiedRole == null || verifiedRole == "quote")
        {
            return Unauthorized(new { valid = false, message = "الجلسة منتهية أو غير صالحة" });
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            var requestedRole = role.Trim().ToLowerInvariant();
            // Reject verifying as legacy quote
            if (requestedRole == "quote")
            {
                return Unauthorized(new { valid = false, message = "تم إلغاء جلسات عروض الأسعار المشتركة." });
            }

            bool allowed = string.Equals(requestedRole, verifiedRole, StringComparison.OrdinalIgnoreCase);
            if (!allowed)
            {
                return Unauthorized(new { valid = false, message = "نوع الصلاحية غير متطابق" });
            }
        }

        return Ok(new
        {
            valid = true,
            role = verifiedRole,
            user = user != null ? new
            {
                id = user.Id,
                fullName = user.FullName,
                email = user.Email
            } : null
        });
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
        if (targetRole == "quote")
        {
            return BadRequest(new { success = false, message = "تم إلغاء كلمة المرور المشتركة لعروض الأسعار. يتم إدارة حسابات الموظفين المعتمدة من تبويب حسابات الموظفين." });
        }

        if (targetRole != "admin")
        {
            return BadRequest(new { success = false, message = "يمكن تغيير كلمة مرور لوحة الإدارة فقط." });
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
            message = "تم تحديث كلمة مرور لوحة الإدارة بنجاح وإلغاء الجلسات السابقة."
        });
    }

    [HttpPost("provision-owner")]
    public async Task<IActionResult> ProvisionOwner([FromBody] ProvisionOwnerRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.BootstrapToken))
            return BadRequest(new { success = false, message = "رمز التهيئة مطلوب." });

        if (string.IsNullOrWhiteSpace(request.OwnerPassword) || request.OwnerPassword.Length < 8)
            return BadRequest(new { success = false, message = "يجب أن تكون كلمة المرور 8 أحرف على الأقل." });

        if (request.OwnerPassword != request.ConfirmPassword)
            return BadRequest(new { success = false, message = "كلمة المرور وتأكيد كلمة المرور غير متطابقين." });

        var (success, error) = await _securityService.ProvisionInitialOwnerAsync(request.BootstrapToken, request.OwnerPassword);
        if (!success)
            return BadRequest(new { success = false, message = error ?? "فشلت عملية تهيئة المالك." });

        return Ok(new { success = true, message = "تمت تهيئة حساب المالك بنجاح لأول مرة." });
    }

    [HttpPost("recover-owner")]
    public async Task<IActionResult> RecoverOwner([FromBody] RecoverOwnerRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.RecoveryKey))
            return BadRequest(new { success = false, message = "مفتاح الاستعادة مطلوب." });

        if (string.IsNullOrWhiteSpace(request.NewOwnerPassword) || request.NewOwnerPassword.Length < 8)
            return BadRequest(new { success = false, message = "يجب أن تكون كلمة المرور 8 أحرف على الأقل." });

        if (request.NewOwnerPassword != request.ConfirmPassword)
            return BadRequest(new { success = false, message = "كلمة المرور وتأكيد كلمة المرور غير متطابقين." });

        var (success, error) = await _securityService.RecoverOwnerCredentialAsync(request.RecoveryKey, request.NewOwnerPassword);
        if (!success)
            return BadRequest(new { success = false, message = error ?? "فشلت عملية استعادة حساب المالك." });

        return Ok(new { success = true, message = "تم استعادة حساب المالك وتحديث كلمة المرور وإلغاء الجلسات السابقة بنجاح." });
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
