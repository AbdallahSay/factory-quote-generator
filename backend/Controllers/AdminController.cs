using System.Text.RegularExpressions;
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
    private static readonly HashSet<string> AllowedClientAuditActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "QuoteDownloaded",
        "QuoteSharedWhatsApp",
        "TermAdded",
        "TermEdited",
        "TermDeleted",
        "ClientTelemetryTest",
        "SecurityBatch02TestAction"
    };

    private readonly ApplicationDbContext _db;
    private readonly ISecurityService _securityService;
    private readonly ITelegramDeliveryService _telegramService;
    private readonly IWebHostEnvironment _env;

    public AdminController(
        ApplicationDbContext db, 
        ISecurityService securityService,
        ITelegramDeliveryService telegramService,
        IWebHostEnvironment env)
    {
        _db = db;
        _securityService = securityService;
        _telegramService = telegramService;
        _env = env;
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

        var recipientsMap = await _db.TelegramRecipients
            .ToDictionaryAsync(r => r.ChatId, r => r.DisplayName);

        var quotes = await _db.Quotes
            .Include(q => q.User)
            .Include(q => q.Items)
            .Include(q => q.TelegramDeliveries)
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
                q.StorageFileName,
                q.DocxStorageFileName,
                q.CreatedAt,
                ItemCount = q.Items.Count,
                TelegramDeliveries = q.TelegramDeliveries.Select(td => new
                {
                    td.ChatId,
                    DisplayName = recipientsMap.ContainsKey(td.ChatId) ? recipientsMap[td.ChatId] : td.ChatId,
                    td.Status,
                    td.TelegramMessageId,
                    td.SentAt,
                    td.RetryCount,
                    td.ErrorMessage
                }).ToList()
            })
            .ToListAsync();

        // Attach fresh document capability tokens so admin links open directly without auth headers (F03)
        var enrichedQuotes = quotes.Select(q =>
        {
            var pdfIdent = q.StorageFileName ?? q.QuoteNumber;
            var docxIdent = q.DocxStorageFileName ?? q.QuoteNumber;
            var pdfToken = _securityService.GenerateDocumentToken(pdfIdent, "pdf", q.CreatedByUserId, TimeSpan.FromDays(7));
            var docxToken = _securityService.GenerateDocumentToken(docxIdent, "docx", q.CreatedByUserId, TimeSpan.FromDays(7));

            return new
            {
                q.Id,
                q.QuoteNumber,
                q.CreatedByUserId,
                q.CreatedBy,
                q.CreatedByFullName,
                q.ClientName,
                q.ClientPhone,
                q.ContactPerson,
                q.ContactTitle,
                q.ProjectName,
                q.Location,
                q.IssuerName,
                q.IssuerJobTitle,
                q.IssuerPrefix,
                q.TotalAmount,
                PdfUrl = AppendOrUpdateToken(q.PdfUrl, pdfToken),
                DocxUrl = AppendOrUpdateToken(q.DocxUrl, docxToken),
                q.CreatedAt,
                q.ItemCount,
                q.TelegramDeliveries
            };
        });

        return Ok(enrichedQuotes);
    }

    [HttpGet("quotes/legacy-document-audit")]
    public async Task<IActionResult> LegacyDocumentAudit()
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var appDataQuotes = Path.Combine(_env.ContentRootPath, "App_Data", "quotes");
        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var wwwrootQuotes = Path.Combine(webRoot, "quotes");

        var quotes = await _db.Quotes
            .AsNoTracking()
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync();

        var auditEntries = new List<object>();
        var filenameUsage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        int existingPhysicalCount = 0;
        int missingPhysicalCount = 0;

        foreach (var q in quotes)
        {
            var effectiveFileName = q.StorageFileName;
            if (string.IsNullOrEmpty(effectiveFileName) && !string.IsNullOrEmpty(q.PdfUrl))
            {
                var raw = q.PdfUrl.Split('?')[0];
                effectiveFileName = Path.GetFileName(Uri.UnescapeDataString(raw));
            }

            bool fileExists = false;
            long? fileSize = null;
            string? fileSha256 = null;

            if (!string.IsNullOrEmpty(effectiveFileName))
            {
                if (!filenameUsage.ContainsKey(effectiveFileName))
                {
                    filenameUsage[effectiveFileName] = new List<string>();
                }
                filenameUsage[effectiveFileName].Add(q.QuoteNumber);

                var p1 = Path.Combine(appDataQuotes, effectiveFileName);
                var p2 = Path.Combine(wwwrootQuotes, effectiveFileName);
                var matchedPath = System.IO.File.Exists(p1) ? p1 : (System.IO.File.Exists(p2) ? p2 : null);

                if (matchedPath != null)
                {
                    fileExists = true;
                    existingPhysicalCount++;
                    var bytes = await System.IO.File.ReadAllBytesAsync(matchedPath);
                    fileSize = bytes.Length;
                    fileSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                }
                else
                {
                    missingPhysicalCount++;
                }
            }
            else
            {
                missingPhysicalCount++;
            }

            auditEntries.Add(new
            {
                quoteId = q.Id,
                quoteNumber = q.QuoteNumber,
                clientName = q.ClientName,
                projectName = q.ProjectName,
                storageFileName = q.StorageFileName,
                pdfSha256 = q.PdfSha256 ?? fileSha256,
                pdfSizeBytes = q.PdfSizeBytes ?? fileSize,
                physicalFileExists = fileExists,
                createdAt = q.CreatedAt
            });
        }

        var collisions = filenameUsage
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => new
            {
                fileName = kv.Key,
                sharedQuoteNumbers = kv.Value,
                collisionCount = kv.Value.Count
            })
            .ToList();

        return Ok(new
        {
            totalQuotes = quotes.Count,
            existingPhysicalCount,
            missingPhysicalCount,
            collisionCount = collisions.Count,
            collisions,
            auditEntries
        });
    }

    [HttpPost("quotes/{id:int}/retry-telegram")]
    [HttpPost("quotes/{id:int}/telegram-deliveries/{routeChatId}/retry")]
    public async Task<IActionResult> RetryTelegramDelivery(
        int id, 
        [FromQuery] string? chatId = null, 
        [FromRoute] string? routeChatId = null,
        [FromQuery] bool allowNewRecipient = false)
    {
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("admin", "owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());
        var targetChatId = !string.IsNullOrWhiteSpace(routeChatId) ? routeChatId : chatId;

        var quote = await _db.Quotes
            .Include(q => q.TelegramDeliveries)
            .FirstOrDefaultAsync(q => q.Id == id);
        if (quote == null)
        {
            return NotFound(new { success = false, message = "عرض السعر غير موجود." });
        }

        // Validate recipient registration and active status strictly (F09)
        if (!string.IsNullOrWhiteSpace(targetChatId))
        {
            var trimmed = targetChatId.Trim();
            var recipient = await _db.TelegramRecipients.FirstOrDefaultAsync(r => r.ChatId == trimmed);
            if (recipient == null)
            {
                return BadRequest(new { success = false, message = "معرّف المحادثة غير مسجل في قائمة المستلمين المعتمدين." });
            }

            if (!recipient.IsActive)
            {
                return BadRequest(new { success = false, message = "مستلم المحادثة غير مفعّل حالياً في النظام." });
            }

            bool wasOriginal = quote.TelegramDeliveries.Any(d => d.ChatId == trimmed);
            if (!wasOriginal)
            {
                if (!allowNewRecipient)
                {
                    return BadRequest(new { success = false, message = "لا يمكن إعادة الإرسال لمستلم لم يكن ضمن المستلمين المعتمدين الأصليين لعرض السعر بدون إذن صريح." });
                }

                // Log authoritative audited action for historical delivery to a new recipient (F08, F09)
                var auditLog = new AuditLog
                {
                    UserId = actorId,
                    Action = "HistoricalQuoteDispatchedToNewRecipient",
                    EntityId = $"QuoteId={quote.Id}, QuoteNumber={quote.QuoteNumber}, ChatId={trimmed}",
                    Timestamp = DateTime.UtcNow
                };
                _db.AuditLogs.Add(auditLog);
                await _db.SaveChangesAsync();
            }
        }

        try
        {
            var results = await _telegramService.RetryQuoteDeliveryAsync(id, targetChatId, allowNewRecipient);

            // Log authoritative delivery retry audit event (F08)
            var retryAudit = new AuditLog
            {
                UserId = actorId,
                Action = "TelegramDeliveryRetried",
                EntityId = $"QuoteId={quote.Id}, QuoteNumber={quote.QuoteNumber}, ChatId={targetChatId ?? "ALL"}",
                Timestamp = DateTime.UtcNow
            };
            _db.AuditLogs.Add(retryAudit);
            await _db.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                quoteId = id,
                quoteNumber = quote.QuoteNumber,
                chatId = targetChatId,
                results = results
            });
        }
        catch (ArgumentException aex)
        {
            return BadRequest(new { success = false, message = aex.Message });
        }
        catch (InvalidOperationException ioex)
        {
            return BadRequest(new { success = false, message = ioex.Message });
        }
    }

    [HttpGet("telegram/recipients")]
    public async Task<IActionResult> GetTelegramRecipients()
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var recipients = await _db.TelegramRecipients
            .OrderByDescending(r => r.IsActive)
            .ThenBy(r => r.Id)
            .ToListAsync();

        var chatIds = recipients.Select(r => r.ChatId).ToList();

        var deliveryStats = await _db.QuoteTelegramDeliveries
            .Where(d => chatIds.Contains(d.ChatId))
            .GroupBy(d => d.ChatId)
            .Select(g => new
            {
                ChatId = g.Key,
                TotalSent = g.Count(d => d.Status == "Sent"),
                TotalFailed = g.Count(d => d.Status == "Failed"),
                LastDeliveryAt = g.Max(d => d.SentAt),
                LastStatus = g.OrderByDescending(d => d.CreatedAt).Select(d => d.Status).FirstOrDefault()
            })
            .ToListAsync();

        var statsMap = deliveryStats.ToDictionary(s => s.ChatId);

        var result = recipients.Select(r => new TelegramRecipientResponseDto
        {
            Id = r.Id,
            ChatId = r.ChatId,
            DisplayName = r.DisplayName,
            IsActive = r.IsActive,
            CreatedAt = r.CreatedAt,
            TotalSent = statsMap.TryGetValue(r.ChatId, out var s) ? s.TotalSent : 0,
            TotalFailed = statsMap.TryGetValue(r.ChatId, out var s2) ? s2.TotalFailed : 0,
            LastDeliveryAt = statsMap.TryGetValue(r.ChatId, out var s3) ? s3.LastDeliveryAt : null,
            LastStatus = statsMap.TryGetValue(r.ChatId, out var s4) ? s4.LastStatus : null
        });

        return Ok(result);
    }

    [HttpPost("telegram/recipients")]
    public async Task<IActionResult> AddTelegramRecipient([FromBody] CreateTelegramRecipientRequestDto dto)
    {
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("admin", "owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());

        if (string.IsNullOrWhiteSpace(dto.ChatId))
        {
            return BadRequest(new { success = false, message = "معرف المحادثة (Chat ID) مطلوب." });
        }

        var trimmedChatId = dto.ChatId.Trim();
        var existing = await _db.TelegramRecipients.FirstOrDefaultAsync(r => r.ChatId == trimmedChatId);
        if (existing != null)
        {
            return BadRequest(new { success = false, message = "معرف المحادثة مسجل بالفعل مسبقاً." });
        }

        var recipient = new TelegramRecipient
        {
            ChatId = trimmedChatId,
            DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? $"مستلم {trimmedChatId}" : dto.DisplayName.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.TelegramRecipients.Add(recipient);

        // Authoritative server audit log (F08)
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = "TelegramRecipientAdded",
            EntityId = $"ChatId={trimmedChatId}, Name={recipient.DisplayName}",
            Timestamp = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        return Ok(new { success = true, recipient, message = "تمت إضافة مستلم التيليجرام بنجاح." });
    }

    [HttpPut("telegram/recipients/{id:int}")]
    public async Task<IActionResult> UpdateTelegramRecipient(int id, [FromBody] UpdateTelegramRecipientRequestDto dto)
    {
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("admin", "owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());

        var recipient = await _db.TelegramRecipients.FindAsync(id);
        if (recipient == null)
        {
            return NotFound(new { success = false, message = "مستلم التيليجرام غير موجود." });
        }

        if (!string.IsNullOrWhiteSpace(dto.DisplayName))
        {
            recipient.DisplayName = dto.DisplayName.Trim();
        }
        if (dto.IsActive.HasValue)
        {
            recipient.IsActive = dto.IsActive.Value;
        }

        // Authoritative server audit log (F08)
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = "TelegramRecipientUpdated",
            EntityId = $"RecipientId={id}, ChatId={recipient.ChatId}",
            Timestamp = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        return Ok(new { success = true, recipient, message = "تم تحديث بيانات المستلم بنجاح." });
    }

    [HttpPost("telegram/recipients/{id:int}/toggle-active")]
    public async Task<IActionResult> ToggleTelegramRecipientActive(int id, [FromBody] ToggleActiveRequestDto? dto = null)
    {
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("admin", "owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());

        var recipient = await _db.TelegramRecipients.FindAsync(id);
        if (recipient == null)
        {
            return NotFound(new { success = false, message = "مستلم التيليجرام غير موجود." });
        }

        if (dto?.IsActive.HasValue == true)
        {
            recipient.IsActive = dto.IsActive.Value;
        }
        else
        {
            recipient.IsActive = !recipient.IsActive;
        }

        // Authoritative server audit log (F08)
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = recipient.IsActive ? "TelegramRecipientActivated" : "TelegramRecipientDeactivated",
            EntityId = $"RecipientId={id}, ChatId={recipient.ChatId}",
            Timestamp = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            isActive = recipient.IsActive,
            message = recipient.IsActive ? "تم تفعيل المستلم بنجاح." : "تم تعطيل المستلم بنجاح."
        });
    }

    [HttpDelete("telegram/recipients/{id:int}")]
    public async Task<IActionResult> DeleteTelegramRecipient(int id)
    {
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("admin", "owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());

        var recipient = await _db.TelegramRecipients.FindAsync(id);
        if (recipient == null)
        {
            return NotFound(new { success = false, message = "مستلم التيليجرام غير موجود." });
        }

        bool hasDeliveries = await _db.QuoteTelegramDeliveries.AnyAsync(d => d.ChatId == recipient.ChatId);
        if (hasDeliveries)
        {
            recipient.IsActive = false;

            _db.AuditLogs.Add(new AuditLog
            {
                UserId = actorId,
                Action = "TelegramRecipientDeactivated",
                EntityId = $"RecipientId={id}, ChatId={recipient.ChatId}",
                Timestamp = DateTime.UtcNow
            });

            await _db.SaveChangesAsync();
            return Ok(new
            {
                success = true,
                deactivated = true,
                message = "تم تعطيل المستلم بدلاً من حذفه للحفاظ على سجلات عروض الأسعار السابقة."
            });
        }

        _db.TelegramRecipients.Remove(recipient);

        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = "TelegramRecipientDeleted",
            EntityId = $"RecipientId={id}, ChatId={recipient.ChatId}",
            Timestamp = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "تم حذف المستلم بنجاح."
        });
    }

    [HttpGet("telegram-info")]
    public async Task<IActionResult> GetTelegramInfo()
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        // Strictly read-only bot status inspection (F11)
        var info = await _telegramService.GetBotInfoAsync();
        return Ok(info);
    }

    [HttpPost("telegram/discover-updates")]
    public async Task<IActionResult> DiscoverTelegramUpdates()
    {
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { success = false, message = "غير مصرح. وظيفة فحص المحادثات مقتصرة على مالك النظام فقط." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());
        var chats = await _telegramService.DiscoverRecentChatsAsync();

        // Authoritative server audit log for explicit updates discovery (F08, F11)
        var audit = new AuditLog
        {
            UserId = actorId,
            Action = "TelegramUpdatesDiscovered",
            EntityId = $"DiscoveredCount={chats.Count}",
            Timestamp = DateTime.UtcNow
        };
        _db.AuditLogs.Add(audit);
        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            count = chats.Count,
            recentChats = chats
        });
    }

    [HttpGet("quotes/{id:int}/verify-telegram-pdf")]
    public async Task<IActionResult> VerifyTelegramPdf(int id, [FromQuery] string fileId)
    {
        if (!await IsAuthorizedAdminAsync())
        {
            return Unauthorized(new { message = "غير مصرح. يرجى إدخال كلمة مرور لوحة الإدارة." });
        }

        if (string.IsNullOrWhiteSpace(fileId))
        {
            return BadRequest(new { success = false, message = "معرّف الملف fileId مطلوب للتحقق." });
        }

        var result = await _telegramService.VerifyDeliveredPdfAsync(fileId, id);
        return Ok(result);
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
        var (isValid, user, role) = await _securityService.ValidateUserTokenAsync(token);
        if (!isValid || user == null)
        {
            return Unauthorized(new { message = "غير مصرح بتسجيل التدقيق." });
        }

        // 1. Validate Action is present and within allowable bounds (F08)
        if (string.IsNullOrWhiteSpace(dto.Action))
        {
            return BadRequest(new { success = false, message = "نوع حدث التدقيق مطلوب." });
        }

        var trimmedAction = dto.Action.Trim();
        if (trimmedAction.Length > 50)
        {
            return BadRequest(new { success = false, message = "حجم نوع الحدث يتجاوز الحد المسموح (50 حرفاً)." });
        }

        // 2. Client-submitted action allowlist enforcement (F08 item 3)
        if (!AllowedClientAuditActions.Contains(trimmedAction))
        {
            return BadRequest(new { success = false, message = "نوع حدث التدقيق غير مصرح به من واجهة العميل." });
        }

        // 3. EntityId length validation (F08 item 4)
        if (dto.EntityId != null && dto.EntityId.Length > 100)
        {
            return BadRequest(new { success = false, message = "حجم معرّف الكيان يتجاوز الحد المسموح (100 حرفاً)." });
        }

        // 4. Object ownership validation for quotation telemetry (F08 item 4)
        if (role == "employee" && (trimmedAction.Equals("QuoteDownloaded", StringComparison.OrdinalIgnoreCase) ||
                                   trimmedAction.Equals("QuoteSharedWhatsApp", StringComparison.OrdinalIgnoreCase)))
        {
            var entity = dto.EntityId ?? "";
            var colonIdx = entity.IndexOf(':');
            var rawCandidate = (colonIdx >= 0 ? entity.Substring(colonIdx + 1) : entity).Trim();
            if (!string.IsNullOrEmpty(rawCandidate))
            {
                var quote = await _db.Quotes.AsNoTracking().FirstOrDefaultAsync(q => q.QuoteNumber == rawCandidate);
                if (quote != null && quote.UserId != user.Id && quote.CreatedByUserId != user.Id)
                {
                    return StatusCode(403, new { success = false, message = "غير مصرح بتسجيل التدقيق لعرض سعر خاص بموظف آخر." });
                }
            }
        }

        // 5. Derive audit actor exclusively from authenticated server identity (F08 items 1 & 2)
        var audit = new AuditLog
        {
            UserId = user.Id,
            Action = trimmedAction,
            EntityId = dto.EntityId?.Trim(),
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
        if (!await IsAuthorizedOwnerAsync())
        {
            return Unauthorized(new { success = false, message = "غير مصرح. وظائف إدارة حسابات الموظفين مقتصرة على مالك النظام فقط." });
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
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { success = false, message = "غير مصرح. وظائف إدارة حسابات الموظفين مقتصرة على مالك النظام فقط." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());

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

        // Authoritative server audit log (F08)
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = "EmployeeCreated",
            EntityId = $"EmployeeId={newEmployee.Id}, Email={newEmployee.Email}",
            Timestamp = DateTime.UtcNow
        });

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
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { success = false, message = "غير مصرح. وظائف إدارة حسابات الموظفين مقتصرة على مالك النظام فقط." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());

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

        // Authoritative server audit log (F08)
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = "EmployeeUpdated",
            EntityId = $"EmployeeId={employee.Id}, Email={employee.Email}",
            Timestamp = DateTime.UtcNow
        });

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
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { success = false, message = "غير مصرح. وظائف إدارة حسابات الموظفين مقتصرة على مالك النظام فقط." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());

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

        // Authoritative server audit log (F08)
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = "EmployeePasswordReset",
            EntityId = $"EmployeeId={employee.Id}, Email={employee.Email}",
            Timestamp = DateTime.UtcNow
        });

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
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { success = false, message = "غير مصرح. وظائف إدارة حسابات الموظفين مقتصرة على مالك النظام فقط." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());

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

        // Authoritative server audit log (F08)
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = employee.IsActive ? "EmployeeActivated" : "EmployeeDeactivated",
            EntityId = $"EmployeeId={employee.Id}, Email={employee.Email}",
            Timestamp = DateTime.UtcNow
        });

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
        var (isAuthorized, actorUser, _) = await GetAuthenticatedActorAsync("owner");
        if (!isAuthorized)
        {
            return Unauthorized(new { success = false, message = "غير مصرح. وظائف إدارة حسابات الموظفين مقتصرة على مالك النظام فقط." });
        }

        var actorId = actorUser?.Id ?? (await GetFallbackAdminUserIdAsync());

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

        // Authoritative server audit log (F08)
        _db.AuditLogs.Add(new AuditLog
        {
            UserId = actorId,
            Action = "EmployeeDeleted",
            EntityId = $"EmployeeId={employee.Id}, Email={employee.Email}",
            Timestamp = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "تم حذف الحساب بنجاح."
        });
    }

    private async Task<(bool isAuthorized, User? user, string? role)> GetAuthenticatedActorAsync(params string[] allowedRoles)
    {
        var token = ExtractBearerToken();
        var (isValid, user, role) = await _securityService.ValidateUserTokenAsync(token);
        if (!isValid || string.IsNullOrEmpty(role)) return (false, null, null);
        if (allowedRoles.Length > 0 && !allowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
            return (false, user, role);
        return (true, user, role);
    }

    private async Task<int> GetFallbackAdminUserIdAsync()
    {
        var admin = await _db.Users.FirstOrDefaultAsync(u => u.Role == "Admin") ?? await _db.Users.FirstOrDefaultAsync();
        return admin?.Id ?? 1;
    }

    private async Task<bool> IsAuthorizedOwnerAsync()
    {
        var token = ExtractBearerToken();
        var (isValid, _) = await _securityService.ValidateTokenAsync(token, "owner");
        return isValid;
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

    private static string AppendOrUpdateToken(string? url, string token)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var clean = url.Split('?')[0];
        return $"{clean}?token={Uri.EscapeDataString(token)}";
    }
}
