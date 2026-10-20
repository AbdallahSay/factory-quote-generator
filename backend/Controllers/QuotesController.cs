using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.DTOs;
using FactoryQuoteApi.Models;
using FactoryQuoteApi.Services;

namespace FactoryQuoteApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class QuotesController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IWordQuoteGeneratorService _wordGenerator;
    private readonly IPdfConverterService _pdfConverter;
    private readonly IBackgroundTaskQueue _backgroundQueue;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<QuotesController> _logger;
    private readonly ISecurityService _securityService;
    private readonly IQuoteSequenceService _sequenceService;
    private readonly ITelegramDeliveryService _telegramService;
    private readonly INotificationService _notificationService;

    public QuotesController(
        ApplicationDbContext db,
        IWordQuoteGeneratorService wordGenerator,
        IPdfConverterService pdfConverter,
        IBackgroundTaskQueue backgroundQueue,
        IConfiguration configuration,
        IWebHostEnvironment env,
        ILogger<QuotesController> logger,
        ISecurityService securityService,
        IQuoteSequenceService sequenceService,
        ITelegramDeliveryService telegramService,
        INotificationService notificationService)
    {
        _db = db;
        _wordGenerator = wordGenerator;
        _pdfConverter = pdfConverter;
        _backgroundQueue = backgroundQueue;
        _configuration = configuration;
        _env = env;
        _logger = logger;
        _securityService = securityService;
        _sequenceService = sequenceService;
        _telegramService = telegramService;
        _notificationService = notificationService;
    }

    [HttpPost("generate")]
    public async Task<ActionResult<QuoteResponseDto>> GenerateQuote([FromBody] QuoteRequestDto request)
    {
        string? token = ExtractBearerToken();
        var (isValid, authenticatedUser, authRole) = await _securityService.ValidateUserTokenAsync(token);
        if (!isValid || authenticatedUser == null)
        {
            return Unauthorized(new { message = "غير مصرح. يرجى تسجيل الدخول بحساب موظف معتمد لإصدار عروض الأسعار." });
        }

        bool hasDynamicRows = request.Rows != null && request.Rows.Count > 0;
        bool hasItems = request.Items != null && request.Items.Count > 0;

        if (!hasDynamicRows && !hasItems)
        {
            return BadRequest(new { message = "Quote must contain at least one item or row." });
        }

        try
        {
            request.IssuerJobTitle = WordQuoteGeneratorService.CleanArabicIssuerText(request.IssuerJobTitle);
            request.IssuerName = WordQuoteGeneratorService.CleanArabicIssuerText(request.IssuerName);
            request.IssuerPrefix = WordQuoteGeneratorService.CleanArabicIssuerText(request.IssuerPrefix);
            var user = authenticatedUser;

            // 2. Extract Client & Project Details
            var clientName = !string.IsNullOrWhiteSpace(request.ClientName) 
                ? request.ClientName.Trim() 
                : "شركة اتريم للمقاولات والاعمال المتخصصة";
            var clientEmail = !string.IsNullOrWhiteSpace(request.ClientEmail) ? request.ClientEmail.Trim() : null;
            var clientPhone = !string.IsNullOrWhiteSpace(request.ClientPhone) ? request.ClientPhone.Trim() : null;
            var contactPerson = request.ContactPerson?.Trim() ?? string.Empty;
            var projectName = !string.IsNullOrWhiteSpace(request.ProjectName) ? request.ProjectName.Trim() : string.Empty;
            var location = request.Location?.Trim() ?? string.Empty;

            // 3. Map dynamic rows or items to database entities
            var quoteItems = new List<QuoteItem>();
            if (hasItems)
            {
                quoteItems = request.Items!.Select(i => new QuoteItem
                {
                    ProductName = i.ProductName,
                    Size = i.Size ?? string.Empty,
                    Capacity = i.Capacity ?? string.Empty,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    LineTotal = i.Quantity * i.UnitPrice
                }).ToList();
            }
            else if (hasDynamicRows)
            {
                if (request.Headers != null)
                {
                    request.Headers = request.Headers.Where(h => !WordQuoteGeneratorService.IsForbiddenColumn(h)).ToList();
                }

                foreach (var row in request.Rows!)
                {
                    string name = GetRowValue(row, "النوع", "الصنف", "اسم المنتج", "Product", "Type") 
                                  ?? row.Values.FirstOrDefault() ?? "صنف";
                    string size = GetRowValue(row, "المقاس", "Size") ?? string.Empty;
                    string cap = GetRowValue(row, "الحمولة", "الوحدة", "Capacity") ?? string.Empty;
                    decimal price = ParseDecimal(GetRowValue(row, "السعر", "سعر الألف", "سعر الالف", "Price", "UnitPrice"), 0);

                    quoteItems.Add(new QuoteItem
                    {
                        ProductName = name,
                        Size = size,
                        Capacity = cap,
                        Quantity = 0,
                        UnitPrice = price,
                        LineTotal = 0
                    });
                }

                // If request.Items is empty, fill it for template fallback
                request.Items = quoteItems.Select(q => new ProductItemDto
                {
                    ProductName = q.ProductName,
                    Size = q.Size,
                    Capacity = q.Capacity,
                    Quantity = 0,
                    UnitPrice = q.UnitPrice
                }).ToList();
            }

            // Calculate TotalAmount securely on the server side by summing up the LineTotal of all items
            var calculatedTotal = quoteItems.Sum(item => item.LineTotal);
            var totalAmount = calculatedTotal > 0 
                ? calculatedTotal 
                : (request.TotalAmount.HasValue && request.TotalAmount.Value > 0 ? request.TotalAmount.Value : 0);

            var quoteDate = request.QuoteDate ?? DateTime.Now;
            // Outgoing number format: [UniqueSequence]-[dd]-[MM]-[yyyy] with 6 digits (e.g. 000123-08-10-2026)
            var quoteNumber = await _sequenceService.GetNextQuoteNumberAsync(quoteDate);

            // 4. Secure File Paths in App_Data/quotes (outside publicly served wwwroot)
            var appDataQuotesDir = Path.Combine(_env.ContentRootPath, "App_Data", "quotes");
            Directory.CreateDirectory(appDataQuotesDir);

            // Generate unique, immutable storage filenames per quotation to prevent overwrites (F15)
            var storageGuid = Guid.NewGuid().ToString("N");
            var docxStorageName = $"quote_{quoteNumber}_{storageGuid}.docx";
            var pdfStorageName = $"quote_{quoteNumber}_{storageGuid}.pdf";
            var docxPath = Path.Combine(appDataQuotesDir, docxStorageName);
            var pdfPath = Path.Combine(appDataQuotesDir, pdfStorageName);

            // Locate Template
            var templatePath = Path.Combine(_env.ContentRootPath, "..", "Templates", "عرض سعر شركة اتريم.docx");
            if (!System.IO.File.Exists(templatePath))
            {
                templatePath = Path.Combine(_env.ContentRootPath, "Templates", "عرض سعر شركة اتريم.docx");
            }

            // 5. Generate Word Document
            await _wordGenerator.GenerateQuoteDocumentAsync(templatePath, docxPath, request, quoteNumber, totalAmount);

            // 6. Convert to PDF
            await _pdfConverter.ConvertDocxToPdfAsync(docxPath, pdfPath);

            // Compute PDF file integrity metadata (SHA-256 and byte size) (F15)
            var pdfBytes = await System.IO.File.ReadAllBytesAsync(pdfPath);
            var pdfSizeBytes = (long)pdfBytes.Length;
            var pdfSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pdfBytes));

            // Generate scoped document capability tokens with 7-day validity (F03)
            var pdfToken = _securityService.GenerateDocumentToken(pdfStorageName, "pdf", user.Id, TimeSpan.FromDays(7));
            var docxToken = _securityService.GenerateDocumentToken(docxStorageName, "docx", user.Id, TimeSpan.FromDays(7));

            // Generate URLs with signed capability tokens
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var pdfUrl = $"{baseUrl}/quotes/{Uri.EscapeDataString(pdfStorageName)}?token={Uri.EscapeDataString(pdfToken)}";
            var docxUrl = $"{baseUrl}/quotes/{Uri.EscapeDataString(docxStorageName)}?token={Uri.EscapeDataString(docxToken)}";

            // 7. Save Quote & Items to SQL Server Database with immutable storage metadata (F15)
            var quote = new Quote
            {
                QuoteNumber = quoteNumber,
                UserId = user.Id,
                CreatedByUserId = user.Id,
                ClientName = clientName,
                ClientEmail = clientEmail,
                ClientPhone = clientPhone,
                ContactPerson = contactPerson,
                ContactTitle = request.ContactTitle,
                IssuerName = request.IssuerName,
                IssuerJobTitle = request.IssuerJobTitle,
                IssuerPrefix = request.IssuerPrefix,
                TermsJson = request.Terms != null && request.Terms.Count > 0 
                    ? System.Text.Json.JsonSerializer.Serialize(request.Terms) 
                    : null,
                ProjectName = projectName,
                Location = location,
                TotalAmount = totalAmount,
                PdfUrl = pdfUrl,
                DocxUrl = docxUrl,
                PdfSha256 = pdfSha256,
                PdfSizeBytes = pdfSizeBytes,
                StorageFileName = pdfStorageName,
                DocxStorageFileName = docxStorageName,
                CreatedAt = DateTime.UtcNow,
                Items = quoteItems
            };

            // Transactional Outbox: record intended approved recipients when quotation is created (F10)
            var activeRecipients = await _db.TelegramRecipients
                .Where(r => r.IsActive)
                .OrderBy(r => r.Id)
                .ToListAsync();

            foreach (var r in activeRecipients)
            {
                quote.TelegramDeliveries.Add(new QuoteTelegramDelivery
                {
                    QuoteNumber = quoteNumber,
                    ChatId = r.ChatId,
                    Status = "Pending",
                    RetryCount = 0,
                    NextRetryAt = DateTime.UtcNow,
                    FileSha256 = pdfSha256,
                    CreatedAt = DateTime.UtcNow
                });
            }

            _db.Quotes.Add(quote);

            // Authoritative server-side audit event with real user.Id (F08)
            var auditLog = new AuditLog
            {
                UserId = user.Id,
                Action = "QuoteGenerated",
                EntityId = quoteNumber,
                Timestamp = DateTime.UtcNow
            };
            _db.AuditLogs.Add(auditLog);

            await _db.SaveChangesAsync();

            var webhookPayload = new WebhookPayloadDto
            {
                Event = "QuoteGenerated",
                QuoteNumber = quoteNumber,
                ClientName = clientName,
                ClientPhone = clientPhone ?? string.Empty,
                ClientEmail = clientEmail,
                TotalAmount = totalAmount,
                PdfUrl = pdfUrl,
                Timestamp = DateTime.UtcNow
            };

            // 8. Automatic Telegram Quotation PDF Delivery (durable transactional outbox, F10)
            try
            {
                await _telegramService.ProcessPendingOutboxAsync(quote.Id, pdfPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to immediately process Telegram outbox for quote {QuoteNumber}; queued for background retry.", quoteNumber);
            }

            // 9. Optional WhatsApp Webhook notification
            if (!string.IsNullOrEmpty(clientPhone))
            {
                try
                {
                    await _notificationService.SendWhatsAppWebhookAsync(webhookPayload);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send WhatsApp webhook for quote {QuoteNumber}.", quoteNumber);
                }
            }
            else
            {
                _logger.LogInformation("Skipping WhatsApp webhook: ClientPhone is empty.");
            }

            // 10. Optional Email notification
            if (!string.IsNullOrEmpty(clientEmail))
            {
                try
                {
                    await _notificationService.SendEmailNotificationAsync(
                        clientEmail,
                        clientName,
                        quoteNumber,
                        totalAmount,
                        pdfPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send Email notification for quote {QuoteNumber}.", quoteNumber);
                }
            }
            else
            {
                _logger.LogInformation("Skipping Email notification: ClientEmail is empty.");
            }

            // Return immediate 200 OK
            return Ok(new QuoteResponseDto
            {
                QuoteId = quote.Id,
                QuoteNumber = quote.QuoteNumber,
                TotalAmount = quote.TotalAmount,
                PdfUrl = pdfUrl,
                DocxUrl = docxUrl,
                Message = "Quote generated successfully.",
                CreatedAt = quote.CreatedAt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate quote.");
            return StatusCode(500, new { message = "An error occurred while generating quote.", error = ex.Message });
        }
    }

    [HttpGet("terms")]
    public IActionResult GetTerms()
    {
        var templatePath = Path.Combine(_env.ContentRootPath, "..", "Templates", "عرض سعر شركة اتريم.docx");
        if (!System.IO.File.Exists(templatePath))
        {
            templatePath = Path.Combine(_env.ContentRootPath, "Templates", "عرض سعر شركة اتريم.docx");
        }

        var terms = _wordGenerator.GetDefaultTerms(templatePath);
        return Ok(terms);
    }

    [HttpPost("preview")]
    public async Task<IActionResult> PreviewQuote([FromBody] QuoteRequestDto request)
    {
        string? token = ExtractBearerToken();
        var (isValid, authenticatedUser, authRole) = await _securityService.ValidateUserTokenAsync(token);
        if (!isValid || authenticatedUser == null)
        {
            return Unauthorized(new { message = "غير مصرح. يرجى تسجيل الدخول بحساب موظف معتمد لمعاينة عروض الأسعار." });
        }

        bool hasDynamicRows = request.Rows != null && request.Rows.Count > 0;
        bool hasItems = request.Items != null && request.Items.Count > 0;

        if (!hasDynamicRows && !hasItems)
        {
            return BadRequest(new { message = "يرجى إدخال صنف واحد على الأقل في عرض السعر." });
        }

        try
        {
            request.IssuerJobTitle = WordQuoteGeneratorService.CleanArabicIssuerText(request.IssuerJobTitle);
            request.IssuerName = WordQuoteGeneratorService.CleanArabicIssuerText(request.IssuerName);
            request.IssuerPrefix = WordQuoteGeneratorService.CleanArabicIssuerText(request.IssuerPrefix);
            var clientName = !string.IsNullOrWhiteSpace(request.ClientName) 
                ? request.ClientName.Trim() 
                : "شركة اتريم للمقاولات والاعمال المتخصصة";
            var projectName = !string.IsNullOrWhiteSpace(request.ProjectName) ? request.ProjectName.Trim() : string.Empty;

            var quoteItems = new List<QuoteItem>();
            if (hasItems)
            {
                quoteItems = request.Items!.Select(i => new QuoteItem
                {
                    ProductName = i.ProductName,
                    Size = i.Size ?? string.Empty,
                    Capacity = i.Capacity ?? string.Empty,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    LineTotal = i.Quantity * i.UnitPrice
                }).ToList();
            }
            else if (hasDynamicRows)
            {
                if (request.Headers != null)
                {
                    request.Headers = request.Headers.Where(h => !WordQuoteGeneratorService.IsForbiddenColumn(h)).ToList();
                }

                foreach (var row in request.Rows!)
                {
                    string name = GetRowValue(row, "النوع", "الصنف", "اسم المنتج", "Product", "Type") 
                                  ?? row.Values.FirstOrDefault() ?? "صنف";
                    string size = GetRowValue(row, "المقاس", "Size") ?? string.Empty;
                    string cap = GetRowValue(row, "الحمولة", "الوحدة", "Capacity") ?? string.Empty;
                    decimal price = ParseDecimal(GetRowValue(row, "السعر", "سعر الألف", "سعر الالف", "Price", "UnitPrice"), 0);

                    quoteItems.Add(new QuoteItem
                    {
                        ProductName = name,
                        Size = size,
                        Capacity = cap,
                        Quantity = 0,
                        UnitPrice = price,
                        LineTotal = 0
                    });
                }

                request.Items = quoteItems.Select(q => new ProductItemDto
                {
                    ProductName = q.ProductName,
                    Size = q.Size,
                    Capacity = q.Capacity,
                    Quantity = 0,
                    UnitPrice = q.UnitPrice
                }).ToList();
            }

            var calculatedTotal = quoteItems.Sum(item => item.LineTotal);
            var totalAmount = calculatedTotal > 0 
                ? calculatedTotal 
                : (request.TotalAmount.HasValue && request.TotalAmount.Value > 0 ? request.TotalAmount.Value : 0);

            var quoteDate = request.QuoteDate ?? DateTime.Now;
            var quoteNumber = await _sequenceService.PeekNextQuoteNumberAsync(quoteDate);

            // Store preview files in App_Data/quotes (outside wwwroot)
            var appDataQuotesDir = Path.Combine(_env.ContentRootPath, "App_Data", "quotes");
            Directory.CreateDirectory(appDataQuotesDir);

            // Safely clean up temporary preview files older than 1 hour
            CleanupOldPreviewFiles(appDataQuotesDir);

            var previewId = Guid.NewGuid().ToString("N");
            var docxFilename = $"preview_{previewId}.docx";
            var pdfFilename = $"preview_{previewId}.pdf";
            var docxPath = Path.Combine(appDataQuotesDir, docxFilename);
            var pdfPath = Path.Combine(appDataQuotesDir, pdfFilename);

            var templatePath = Path.Combine(_env.ContentRootPath, "..", "Templates", "عرض سعر شركة اتريم.docx");
            if (!System.IO.File.Exists(templatePath))
            {
                templatePath = Path.Combine(_env.ContentRootPath, "Templates", "عرض سعر شركة اتريم.docx");
            }

            await _wordGenerator.GenerateQuoteDocumentAsync(templatePath, docxPath, request, quoteNumber, totalAmount);
            await _pdfConverter.ConvertDocxToPdfAsync(docxPath, pdfPath);

            // Previews have authenticated capability tokens with 2-hour validity (F03)
            var previewPdfToken = _securityService.GenerateDocumentToken(pdfFilename, "pdf", authenticatedUser.Id, TimeSpan.FromHours(2));
            var previewDocxToken = _securityService.GenerateDocumentToken(docxFilename, "docx", authenticatedUser.Id, TimeSpan.FromHours(2));

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var pdfUrl = $"{baseUrl}/quotes/{Uri.EscapeDataString(pdfFilename)}?token={Uri.EscapeDataString(previewPdfToken)}";
            var docxUrl = $"{baseUrl}/quotes/{Uri.EscapeDataString(docxFilename)}?token={Uri.EscapeDataString(previewDocxToken)}";

            return Ok(new
            {
                quoteNumber = quoteNumber,
                pdfUrl = pdfUrl,
                docxUrl = docxUrl,
                message = "Preview generated successfully."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate quote preview.");
            return StatusCode(500, new { message = "حدث خطأ أثناء معاينة عرض السعر.", error = ex.Message });
        }
    }

    private static void CleanupOldPreviewFiles(string quotesDir)
    {
        try
        {
            var dir = new DirectoryInfo(quotesDir);
            if (!dir.Exists) return;
            var cutoff = DateTime.UtcNow.AddHours(-1);
            foreach (var file in dir.GetFiles("preview_*.*"))
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    try { file.Delete(); } catch { }
                }
            }
        }
        catch
        {
            // Ignore background file cleanup errors
        }
    }

    [HttpGet("{id:int}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<QuoteReadResponseDto>> GetQuote(int id)
    {
        var (isValid, authenticatedUser, role) = await _securityService.ValidateUserTokenAsync(ExtractBearerToken());
        if (!isValid)
        {
            return Unauthorized();
        }

        var query = _db.Quotes.AsNoTracking().Where(q => q.Id == id);
        if (role != "owner" && role != "admin")
        {
            if (role != "employee" || authenticatedUser == null)
            {
                return NotFound();
            }

            var employeeId = authenticatedUser.Id;
            query = query.Where(q => q.UserId == employeeId && q.CreatedByUserId == employeeId
                && _db.Users.Any(u => u.Role == "Admin")
                && !_db.Users.Any(u => u.Id == employeeId && u.Role == "Admin"));
        }

        var quote = await query.Select(q => new QuoteReadResponseDto
        {
            Id = q.Id,
            QuoteNumber = q.QuoteNumber,
            ClientName = q.ClientName,
            ClientEmail = q.ClientEmail,
            ClientPhone = q.ClientPhone,
            ContactPerson = q.ContactPerson,
            ContactTitle = q.ContactTitle,
            ProjectName = q.ProjectName,
            Location = q.Location,
            IssuerName = q.IssuerName,
            IssuerJobTitle = q.IssuerJobTitle,
            IssuerPrefix = q.IssuerPrefix,
            TermsJson = q.TermsJson,
            TotalAmount = q.TotalAmount,
            PdfUrl = q.PdfUrl,
            DocxUrl = q.DocxUrl,
            CreatedAt = q.CreatedAt,
            Items = q.Items.OrderBy(i => i.Id).Select(i => new QuoteReadItemDto
            {
                Id = i.Id,
                ProductName = i.ProductName,
                Size = i.Size,
                Capacity = i.Capacity,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                LineTotal = i.LineTotal
            }).ToList()
        }).FirstOrDefaultAsync(HttpContext.RequestAborted);

        if (quote == null) return NotFound();

        // Attach fresh capability tokens so authorized users can open documents directly (F03)
        var entity = await _db.Quotes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id, HttpContext.RequestAborted);
        if (entity != null)
        {
            var pdfIdent = entity.StorageFileName ?? entity.QuoteNumber;
            var docxIdent = entity.DocxStorageFileName ?? entity.QuoteNumber;
            var pdfToken = _securityService.GenerateDocumentToken(pdfIdent, "pdf", entity.UserId, TimeSpan.FromDays(7));
            var docxToken = _securityService.GenerateDocumentToken(docxIdent, "docx", entity.UserId, TimeSpan.FromDays(7));
            quote.PdfUrl = AppendOrUpdateToken(quote.PdfUrl, pdfToken);
            quote.DocxUrl = AppendOrUpdateToken(quote.DocxUrl, docxToken);
        }

        return Ok(quote);
    }

    [HttpGet("{id:int}/download")]
    public async Task<IActionResult> DownloadQuoteById(int id, [FromQuery] string fileType = "pdf", [FromQuery] string? token = null)
    {
        var quote = await _db.Quotes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id);
        if (quote == null)
        {
            return NotFound(new { message = "عرض السعر غير موجود." });
        }

        var isDocx = fileType.Equals("docx", StringComparison.OrdinalIgnoreCase);
        var targetFile = isDocx
            ? (quote.DocxStorageFileName ?? $"EG-BLOCK-{quote.QuoteNumber}.docx")
            : (quote.StorageFileName ?? $"EG-BLOCK-{quote.QuoteNumber}.pdf");

        return await DownloadQuoteFile(targetFile, token);
    }

    [HttpGet("/quotes/{fileName}")]
    [HttpGet("download/{fileName}")]
    public async Task<IActionResult> DownloadQuoteFile(string fileName, [FromQuery] string? token = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new { message = "اسم الملف مطلوب." });
        }

        var cleanFileName = Path.GetFileName(Uri.UnescapeDataString(fileName));
        var ext = Path.GetExtension(cleanFileName).ToLowerInvariant();
        var extClean = ext.TrimStart('.');

        bool isAuthorized = false;
        Quote? matchedQuote = null;

        // 1. Validate Scoped Document Capability Token if present (F03)
        if (!string.IsNullOrWhiteSpace(token))
        {
            var (isDocValid, _) = _securityService.ValidateDocumentToken(token, cleanFileName, extClean);
            if (isDocValid)
            {
                isAuthorized = true;
            }
            else
            {
                // Fallback: validate against quote sequence number if available
                matchedQuote = await FindQuoteForFileNameAsync(cleanFileName);
                if (matchedQuote != null)
                {
                    var (isQuoteNoValid, _) = _securityService.ValidateDocumentToken(token, matchedQuote.QuoteNumber, extClean);
                    if (isQuoteNoValid)
                    {
                        isAuthorized = true;
                    }
                }

                if (!isAuthorized)
                {
                    return Unauthorized(new { message = "رمز الوصول للمستند غير صالح أو منتهي الصلاحية." });
                }
            }
        }

        // 2. Validate Session Bearer Token if no capability token (F03)
        if (!isAuthorized)
        {
            var bearerToken = ExtractBearerToken();
            if (string.IsNullOrWhiteSpace(bearerToken))
            {
                return Unauthorized(new { message = "غير مصرح بالوصول إلى هذا المستند. يرجى تسجيل الدخول أو استخدام رابط مصرح به." });
            }

            var (isValidUser, user, role) = await _securityService.ValidateUserTokenAsync(bearerToken);
            if (!isValidUser || user == null)
            {
                return Unauthorized(new { message = "جلسة المستخدم غير صالحة." });
            }

            if (role == "owner" || role == "admin")
            {
                isAuthorized = true;
            }
            else if (role == "employee")
            {
                if (cleanFileName.StartsWith("preview_", StringComparison.OrdinalIgnoreCase))
                {
                    isAuthorized = true;
                }
                else
                {
                    matchedQuote ??= await FindQuoteForFileNameAsync(cleanFileName);
                    if (matchedQuote == null)
                    {
                        return NotFound(new { message = "المستند غير موجود." });
                    }

                    // Strict employee ownership check: Employee A cannot read Employee B's files (F03)
                    if (matchedQuote.UserId == user.Id || matchedQuote.CreatedByUserId == user.Id)
                    {
                        isAuthorized = true;
                    }
                    else
                    {
                        return StatusCode(403, new { message = "غير مصرح لك بالوصول إلى مستندات موظف آخر." });
                    }
                }
            }
            else
            {
                return Unauthorized();
            }
        }

        // 3. Resolve physical file path on disk (F03 + F15)
        matchedQuote ??= await FindQuoteForFileNameAsync(cleanFileName);
        var filePath = ResolvePhysicalFilePath(cleanFileName, matchedQuote);

        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
        {
            return NotFound(new { message = $"الملف {cleanFileName} غير موجود على الخادم." });
        }

        // 4. Preserve readable Arabic download filename where supported (F15)
        string displayFileName = cleanFileName;
        if (matchedQuote != null)
        {
            var safeBase = _wordGenerator.SanitizeFileName(matchedQuote.ClientName, matchedQuote.ProjectName, matchedQuote.QuoteNumber);
            displayFileName = $"{safeBase}{ext}";
        }

        var asciiFallback = $"EG-BLOCK-{Path.GetFileNameWithoutExtension(cleanFileName)}{ext}";

        if (ext == ".pdf")
        {
            // Inline disposition with UTF-8 filename* preserves browser preview and Arabic download names
            Response.Headers["Content-Disposition"] = $"inline; filename=\"{asciiFallback}\"; filename*=UTF-8''{Uri.EscapeDataString(displayFileName)}";
            return PhysicalFile(filePath, "application/pdf");
        }

        var contentType = ext switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream"
        };

        Response.Headers["Content-Disposition"] = $"attachment; filename=\"{asciiFallback}\"; filename*=UTF-8''{Uri.EscapeDataString(displayFileName)}";
        return PhysicalFile(filePath, contentType);
    }

    private async Task<Quote?> FindQuoteForFileNameAsync(string fileName)
    {
        return await _db.Quotes.AsNoTracking().FirstOrDefaultAsync(q =>
            q.StorageFileName == fileName ||
            q.DocxStorageFileName == fileName ||
            (q.PdfUrl != null && q.PdfUrl.Contains(fileName)) ||
            (q.DocxUrl != null && q.DocxUrl.Contains(fileName)) ||
            fileName.Contains(q.QuoteNumber));
    }

    private string? ResolvePhysicalFilePath(string fileName, Quote? quote)
    {
        var appDataQuotes = Path.Combine(_env.ContentRootPath, "App_Data", "quotes");
        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var wwwrootQuotes = Path.Combine(webRoot, "quotes");

        // 1. Direct App_Data/quotes lookup
        var p1 = Path.Combine(appDataQuotes, fileName);
        if (System.IO.File.Exists(p1)) return p1;

        // 2. Direct wwwroot/quotes legacy lookup
        var p2 = Path.Combine(wwwrootQuotes, fileName);
        if (System.IO.File.Exists(p2)) return p2;

        if (quote != null)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            var targetStorageName = ext == ".docx" ? quote.DocxStorageFileName : quote.StorageFileName;
            if (!string.IsNullOrEmpty(targetStorageName))
            {
                var p3 = Path.Combine(appDataQuotes, targetStorageName);
                if (System.IO.File.Exists(p3)) return p3;

                var p4 = Path.Combine(wwwrootQuotes, targetStorageName);
                if (System.IO.File.Exists(p4)) return p4;
            }

            // Search by quote number in App_Data/quotes
            if (Directory.Exists(appDataQuotes))
            {
                var match = Directory.GetFiles(appDataQuotes, $"*{quote.QuoteNumber}*{ext}").FirstOrDefault();
                if (!string.IsNullOrEmpty(match)) return match;
            }

            // Search by quote number in legacy wwwroot/quotes
            if (Directory.Exists(wwwrootQuotes))
            {
                var match = Directory.GetFiles(wwwrootQuotes, $"*{quote.QuoteNumber}*{ext}").FirstOrDefault();
                if (!string.IsNullOrEmpty(match)) return match;
            }
        }

        return null;
    }

    private static string AppendOrUpdateToken(string? url, string token)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var clean = url.Split('?')[0];
        return $"{clean}?token={Uri.EscapeDataString(token)}";
    }

    private static string? GetRowValue(Dictionary<string, string> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            var match = row.FirstOrDefault(kv => kv.Key.Trim().Equals(key, StringComparison.OrdinalIgnoreCase) || kv.Key.Contains(key));
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value.Trim();
        }
        return null;
    }

    private static decimal ParseDecimal(string? text, decimal fallback = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var clean = System.Text.RegularExpressions.Regex.Replace(text, @"[^\d\.\,\-]", "").Replace(",", "");
        return decimal.TryParse(clean, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : fallback;
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
