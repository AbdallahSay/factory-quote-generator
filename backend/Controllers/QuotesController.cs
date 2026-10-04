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

    public QuotesController(
        ApplicationDbContext db,
        IWordQuoteGeneratorService wordGenerator,
        IPdfConverterService pdfConverter,
        IBackgroundTaskQueue backgroundQueue,
        IConfiguration configuration,
        IWebHostEnvironment env,
        ILogger<QuotesController> logger)
    {
        _db = db;
        _wordGenerator = wordGenerator;
        _pdfConverter = pdfConverter;
        _backgroundQueue = backgroundQueue;
        _configuration = configuration;
        _env = env;
        _logger = logger;
    }

    [HttpPost("generate")]
    public async Task<ActionResult<QuoteResponseDto>> GenerateQuote([FromBody] QuoteRequestDto request)
    {
        bool hasDynamicRows = request.Rows != null && request.Rows.Count > 0;
        bool hasItems = request.Items != null && request.Items.Count > 0;

        if (!hasDynamicRows && !hasItems)
        {
            return BadRequest(new { message = "Quote must contain at least one item or row." });
        }

        try
        {
            // 1. Ensure User exists
            var user = await _db.Users.FindAsync(request.UserId);
            if (user == null)
            {
                user = await _db.Users.FirstOrDefaultAsync() ?? new User
                {
                    FullName = "Sales Representative",
                    Email = "sales@factory-eg.com",
                    PasswordHash = "hashed_default_pwd",
                    Role = "Sales"
                };
                if (user.Id == 0)
                {
                    _db.Users.Add(user);
                    await _db.SaveChangesAsync();
                }
            }

            // 2. Extract Client & Project Details
            var clientName = !string.IsNullOrWhiteSpace(request.ClientName) 
                ? request.ClientName.Trim() 
                : "شركة اتريم للمقاولات والاعمال المتخصصة";
            var clientEmail = !string.IsNullOrWhiteSpace(request.ClientEmail) ? request.ClientEmail.Trim() : null;
            var clientPhone = !string.IsNullOrWhiteSpace(request.ClientPhone) ? request.ClientPhone.Trim() : null;
            var contactPerson = request.ContactPerson?.Trim() ?? string.Empty;
            var projectName = request.ProjectName?.Trim() ?? "مشروع اتريم";
            var location = request.Location?.Trim() ?? "القاهرة - مصر";

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
                foreach (var row in request.Rows!)
                {
                    string name = GetRowValue(row, "النوع", "الصنف", "اسم المنتج", "Product", "Type") 
                                  ?? row.Values.FirstOrDefault() ?? "صنف";
                    string size = GetRowValue(row, "المقاس", "Size") ?? string.Empty;
                    string cap = GetRowValue(row, "الحمولة", "الوحدة", "Capacity") ?? string.Empty;
                    decimal qty = ParseDecimal(GetRowValue(row, "الكمية", "Quantity", "Qty"), 1);
                    decimal price = ParseDecimal(GetRowValue(row, "السعر", "سعر الألف", "سعر الالف", "Price", "UnitPrice"), 0);
                    decimal total = ParseDecimal(GetRowValue(row, "الإجمالي", "الاجمالي", "Total", "LineTotal"), qty * price);

                    quoteItems.Add(new QuoteItem
                    {
                        ProductName = name,
                        Size = size,
                        Capacity = cap,
                        Quantity = qty,
                        UnitPrice = price,
                        LineTotal = total
                    });
                }

                // If request.Items is empty, fill it for template fallback
                request.Items = quoteItems.Select(q => new ProductItemDto
                {
                    ProductName = q.ProductName,
                    Size = q.Size,
                    Capacity = q.Capacity,
                    Quantity = q.Quantity,
                    UnitPrice = q.UnitPrice
                }).ToList();
            }

            // Calculate Totals
            var totalAmount = request.TotalAmount.HasValue && request.TotalAmount.Value > 0
                ? request.TotalAmount.Value
                : quoteItems.Sum(item => item.LineTotal);

            var quoteNumber = $"Q-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(1000, 9999)}";

            // 4. File Paths
            var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
            var quotesDir = Path.Combine(webRoot, "quotes");
            System.IO.Directory.CreateDirectory(quotesDir);

            var docxFilename = $"{quoteNumber}.docx";
            var pdfFilename = $"{quoteNumber}.pdf";
            var docxPath = Path.Combine(quotesDir, docxFilename);
            var pdfPath = Path.Combine(quotesDir, pdfFilename);

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

            // Generate URLs
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var pdfUrl = $"{baseUrl}/quotes/{pdfFilename}";
            var docxUrl = $"{baseUrl}/quotes/{docxFilename}";

            // 7. Save Quote & Items to SQL Server Database
            var quote = new Quote
            {
                QuoteNumber = quoteNumber,
                UserId = user.Id,
                ClientName = clientName,
                ClientEmail = clientEmail,
                ClientPhone = clientPhone,
                ContactPerson = contactPerson,
                ProjectName = projectName,
                Location = location,
                TotalAmount = totalAmount,
                PdfUrl = pdfUrl,
                DocxUrl = docxUrl,
                CreatedAt = DateTime.UtcNow,
                Items = quoteItems
            };

            _db.Quotes.Add(quote);

            // Insert AuditLog
            var auditLog = new AuditLog
            {
                UserId = user.Id,
                Action = "Quote_Generated",
                EntityId = quoteNumber,
                Timestamp = DateTime.UtcNow
            };
            _db.AuditLogs.Add(auditLog);

            await _db.SaveChangesAsync();

            // 8. Fire-and-forget Background Tasks for Notifications
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

            await _backgroundQueue.QueueBackgroundWorkItemAsync(async (sp, ct) =>
            {
                var notificationService = sp.GetRequiredService<INotificationService>();
                var logger = sp.GetRequiredService<ILogger<QuotesController>>();

                logger.LogInformation("Processing background notifications for quote {QuoteNumber}...", quoteNumber);

                // Send WhatsApp Webhook only if ClientPhone is provided
                if (!string.IsNullOrEmpty(clientPhone))
                {
                    await notificationService.SendWhatsAppWebhookAsync(webhookPayload, ct);
                }
                else
                {
                    logger.LogInformation("Skipping WhatsApp webhook: ClientPhone is empty.");
                }

                // Send Email Notification only if ClientEmail is provided
                if (!string.IsNullOrEmpty(clientEmail))
                {
                    await notificationService.SendEmailNotificationAsync(
                        clientEmail,
                        clientName,
                        quoteNumber,
                        totalAmount,
                        pdfPath,
                        ct);
                }
                else
                {
                    logger.LogInformation("Skipping Email notification: ClientEmail is empty.");
                }
            });

            // 9. Return immediate 200 OK
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

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Quote>> GetQuote(int id)
    {
        var quote = await _db.Quotes
            .Include(q => q.Items)
            .Include(q => q.User)
            .FirstOrDefaultAsync(q => q.Id == id);

        if (quote == null) return NotFound();
        return Ok(quote);
    }

    [HttpGet("/quotes/{fileName}")]
    public IActionResult DownloadQuoteFile(string fileName)
    {
        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var filePath = Path.Combine(webRoot, "quotes", Path.GetFileName(fileName));
        if (!System.IO.File.Exists(filePath))
        {
            return NotFound(new { message = $"File {fileName} not found." });
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var contentType = ext switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            _ => "application/octet-stream"
        };

        return PhysicalFile(filePath, contentType, fileName);
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
}

