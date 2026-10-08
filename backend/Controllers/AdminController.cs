using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.Models;
using FactoryQuoteApi.Services;

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
