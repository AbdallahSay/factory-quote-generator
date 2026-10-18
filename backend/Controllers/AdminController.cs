using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.Models;
using FactoryQuoteApi.Services;
using FactoryQuoteApi.DTOs;

namespace FactoryQuoteApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdminController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ISecurityService _securityService;

    public AdminController(ApplicationDbContext db, ISecurityService securityService)
    {
        _db = db;
        _securityService = securityService;
    }

    [HttpGet("logs")]
    public async Task<ActionResult<IEnumerable<object>>> GetLogs()
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var logs = await _db.AuditLogs
            .Include(a => a.User)
            .OrderByDescending(a => a.Timestamp)
            .Take(100)
            .Select(a => new
            {
                a.Id,
                a.Action,
                a.EntityId,
                a.Timestamp,
                User = new
                {
                    a.User!.Id,
                    a.User.FullName,
                    a.User.Email,
                    a.User.Role
                }
            })
            .ToListAsync();

        return Ok(logs);
    }

    [HttpGet("quotes")]
    public async Task<ActionResult<IEnumerable<object>>> GetRecentQuotes()
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var quotes = await _db.Quotes
            .Include(q => q.User)
            .Include(q => q.Items)
            .OrderByDescending(q => q.CreatedAt)
            .Take(50)
            .Select(q => new
            {
                q.Id,
                q.QuoteNumber,
                CreatedByUserId = q.CreatedByUserId ?? q.UserId,
                CreatedBy = q.User != null ? q.User.FullName : "غير محدد",
                CreatedByFullName = q.User != null ? q.User.FullName : "غير محدد",
                ClientName = q.ClientName,
                ClientPhone = q.ClientPhone ?? "",
                q.ContactPerson,
                q.ContactTitle,
                q.ProjectName,
                q.Location,
                q.IssuerName,
                q.IssuerJobTitle,
                q.IssuerPrefix,
                q.TotalAmount,
                q.PdfUrl,
                q.DocxUrl,
                q.CreatedAt,
                ItemCount = q.Items.Count
            })
            .ToListAsync();

        return Ok(quotes);
    }

    public class AuditLogRequestDto
    {
        public int? UserId { get; set; } = 1;
        public string Action { get; set; } = string.Empty;
        public string? EntityId { get; set; }
    }

    [HttpPost("audit")]
    public async Task<IActionResult> RecordAudit([FromBody] AuditLogRequestDto dto)
    {
        var token = ExtractBearerToken();
        var (isAuthorized, _) = await _securityService.ValidateTokenAsync(token, "quote", "admin", "owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { message = "غير مصرح بتسجيل التدقيق" });
        }

        var audit = new AuditLog
        {
            UserId = dto.UserId ?? 1,
            Action = dto.Action,
            EntityId = dto.EntityId,
            Timestamp = DateTime.UtcNow
        };
        _db.AuditLogs.Add(audit);
        await _db.SaveChangesAsync();
        return Ok(new { success = true });
    }

    [HttpGet("stats")]
    public async Task<ActionResult<object>> GetDashboardStats()
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var totalQuotes = await _db.Quotes.CountAsync();
        var totalRevenue = await _db.Quotes.SumAsync(q => q.TotalAmount);
        var totalClients = await _db.Clients.CountAsync();
        var totalAuditLogs = await _db.AuditLogs.CountAsync();

        return Ok(new
        {
            TotalQuotes = totalQuotes,
            TotalRevenue = totalRevenue,
            TotalClients = totalClients,
            TotalAuditLogs = totalAuditLogs
        });
    }

    [HttpGet("employees")]
    public async Task<ActionResult<IEnumerable<EmployeeResponseDto>>> GetEmployees()
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var employees = await _db.Users
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new EmployeeResponseDto
            {
                Id = u.Id,
                FullName = u.FullName,
                Email = u.Email,
                Role = u.Role,
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt,
                QuotesCount = u.Quotes.Count
            })
            .ToListAsync();

        return Ok(employees);
    }

    [HttpPost("employees")]
    public async Task<IActionResult> CreateEmployee([FromBody] CreateEmployeeRequestDto request)
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            return BadRequest(new { success = false, message = "الاسم بالكامل مطلوب." });
        }

        var normalizedEmail = request.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return BadRequest(new { success = false, message = "البريد الإلكتروني مطلوب." });
        }

        if (await _db.Users.AnyAsync(u => u.Email.ToLower() == normalizedEmail))
        {
            return BadRequest(new { success = false, message = "البريد الإلكتروني مستخدم بالفعل." });
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 6)
        {
            return BadRequest(new { success = false, message = "يجب أن تكون كلمة المرور 6 أحرف على الأقل." });
        }

        if (request.Password != request.ConfirmPassword)
        {
            return BadRequest(new { success = false, message = "كلمة المرور وتأكيد كلمة المرور غير متطابقين." });
        }

        var newEmployee = new User
        {
            FullName = request.FullName.Trim(),
            Email = normalizedEmail,
            PasswordHash = _securityService.HashPassword(request.Password),
            Role = "Sales",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(newEmployee);
        await _db.SaveChangesAsync();

        return StatusCode(201, new
        {
            success = true,
            message = "تم إنشاء حساب الموظف بنجاح.",
            user = new EmployeeResponseDto
            {
                Id = newEmployee.Id,
                FullName = newEmployee.FullName,
                Email = newEmployee.Email,
                Role = newEmployee.Role,
                IsActive = newEmployee.IsActive,
                CreatedAt = newEmployee.CreatedAt,
                QuotesCount = 0
            }
        });
    }

    [HttpPut("employees/{id}")]
    public async Task<IActionResult> UpdateEmployee(int id, [FromBody] UpdateEmployeeRequestDto request)
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var employee = await _db.Users.FindAsync(id);
        if (employee == null)
        {
            return NotFound(new { success = false, message = "حساب الموظف غير موجود." });
        }

        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            return BadRequest(new { success = false, message = "الاسم بالكامل مطلوب." });
        }

        var normalizedEmail = request.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            return BadRequest(new { success = false, message = "البريد الإلكتروني مطلوب." });
        }

        if (await _db.Users.AnyAsync(u => u.Id != id && u.Email.ToLower() == normalizedEmail))
        {
            return BadRequest(new { success = false, message = "البريد الإلكتروني مستخدم بالفعل لحساب آخر." });
        }

        employee.FullName = request.FullName.Trim();
        employee.Email = normalizedEmail;

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "تم تحديث بيانات حساب الموظف بنجاح.",
            user = new EmployeeResponseDto
            {
                Id = employee.Id,
                FullName = employee.FullName,
                Email = employee.Email,
                Role = employee.Role,
                IsActive = employee.IsActive,
                CreatedAt = employee.CreatedAt,
                QuotesCount = await _db.Quotes.CountAsync(q => q.UserId == id)
            }
        });
    }

    [HttpPost("employees/{id}/reset-password")]
    public async Task<IActionResult> ResetEmployeePassword(int id, [FromBody] ResetEmployeePasswordRequestDto request)
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var employee = await _db.Users.FindAsync(id);
        if (employee == null)
        {
            return NotFound(new { success = false, message = "حساب الموظف غير موجود." });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
        {
            return BadRequest(new { success = false, message = "يجب أن تكون كلمة المرور 6 أحرف على الأقل." });
        }

        if (request.NewPassword != request.ConfirmPassword)
        {
            return BadRequest(new { success = false, message = "كلمة المرور وتأكيد كلمة المرور غير متطابقين." });
        }

        employee.PasswordHash = _securityService.HashPassword(request.NewPassword);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "تمت إعادة تعيين كلمة المرور للموظف بنجاح وإلغاء جلساته السابقة."
        });
    }

    [HttpPost("employees/{id}/toggle-status")]
    public async Task<IActionResult> ToggleEmployeeStatus(int id, [FromBody] ToggleActiveRequestDto? request = null)
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var employee = await _db.Users.FindAsync(id);
        if (employee == null)
        {
            return NotFound(new { success = false, message = "حساب الموظف غير موجود." });
        }

        if (request?.IsActive.HasValue == true)
        {
            employee.IsActive = request.IsActive.Value;
        }
        else
        {
            employee.IsActive = !employee.IsActive;
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            isActive = employee.IsActive,
            message = employee.IsActive ? "تم تفعيل الحساب بنجاح." : "تم إلغاء تفعيل الحساب بنجاح."
        });
    }

    [HttpDelete("employees/{id}")]
    public async Task<IActionResult> DeleteEmployee(int id)
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var employee = await _db.Users.FindAsync(id);
        if (employee == null)
        {
            return NotFound(new { success = false, message = "حساب الموظف غير موجود." });
        }

        bool hasQuotes = await _db.Quotes.AnyAsync(q => q.UserId == id || q.CreatedByUserId == id);
        if (hasQuotes)
        {
            return BadRequest(new
            {
                success = false,
                message = "لا يمكن حذف هذا الحساب نهائياً لوجود عروض أسعار سابقة صادرة منه. يرجى استخدام 'إلغاء التفعيل' لحفظ السجلات التاريخية."
            });
        }

        _db.Users.Remove(employee);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "تم حذف الحساب بنجاح."
        });
    }

    private async Task<bool> IsAuthorizedAdminAsync()
    {
        var token = ExtractBearerToken();
        var (isValid, _) = await _securityService.ValidateTokenAsync(token, "admin", "owner");
        return isValid;
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
