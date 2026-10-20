using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.Models;

namespace FactoryQuoteApi.Services;

public class TelegramDeliveryService : ITelegramDeliveryService
{
    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<TelegramDeliveryService> _logger;

    public TelegramDeliveryService(
        ApplicationDbContext db,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IWebHostEnvironment env,
        ILogger<TelegramDeliveryService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _env = env;
        _logger = logger;
    }

    public async Task EnsureInitializedAsync()
    {
        try
        {
            if (_db.Database.IsSqlServer())
            {
                await _db.Database.ExecuteSqlRawAsync(@"
                    IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'QuoteTelegramDeliveries')
                    BEGIN
                        CREATE TABLE QuoteTelegramDeliveries (
                            Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            QuoteId INT NOT NULL,
                            QuoteNumber NVARCHAR(100) NOT NULL,
                            ChatId NVARCHAR(50) NOT NULL,
                            Status NVARCHAR(20) NOT NULL,
                            TelegramMessageId NVARCHAR(100) NULL,
                            ErrorMessage NVARCHAR(2000) NULL,
                            RetryCount INT NOT NULL DEFAULT 0,
                            SentAt DATETIME2 NULL,
                            CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                            ClaimToken NVARCHAR(100) NULL,
                            ClaimedAt DATETIME2 NULL,
                            ClaimExpiresAt DATETIME2 NULL,
                            NextRetryAt DATETIME2 NULL,
                            LastAttemptAt DATETIME2 NULL,
                            FileSha256 NVARCHAR(64) NULL,
                            CONSTRAINT FK_QuoteTelegramDeliveries_Quotes_QuoteId FOREIGN KEY (QuoteId) REFERENCES Quotes(Id) ON DELETE CASCADE
                        );

                        CREATE UNIQUE NONCLUSTERED INDEX IX_QuoteTelegramDeliveries_QuoteId_ChatId 
                        ON QuoteTelegramDeliveries (QuoteId, ChatId);

                        CREATE NONCLUSTERED INDEX IX_QuoteTelegramDeliveries_Status_NextRetryAt 
                        ON QuoteTelegramDeliveries (Status, NextRetryAt);
                    END
                    ELSE
                    BEGIN
                        IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('QuoteTelegramDeliveries') AND name = 'ClaimToken')
                        BEGIN
                            ALTER TABLE QuoteTelegramDeliveries ADD ClaimToken NVARCHAR(100) NULL;
                            ALTER TABLE QuoteTelegramDeliveries ADD ClaimedAt DATETIME2 NULL;
                            ALTER TABLE QuoteTelegramDeliveries ADD ClaimExpiresAt DATETIME2 NULL;
                            ALTER TABLE QuoteTelegramDeliveries ADD NextRetryAt DATETIME2 NULL;
                            ALTER TABLE QuoteTelegramDeliveries ADD LastAttemptAt DATETIME2 NULL;
                            ALTER TABLE QuoteTelegramDeliveries ADD FileSha256 NVARCHAR(64) NULL;
                        END
                        IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('QuoteTelegramDeliveries') AND name = 'IX_QuoteTelegramDeliveries_Status_NextRetryAt')
                        BEGIN
                            CREATE NONCLUSTERED INDEX IX_QuoteTelegramDeliveries_Status_NextRetryAt 
                            ON QuoteTelegramDeliveries (Status, NextRetryAt);
                        END
                    END

                    IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TelegramRecipients')
                    BEGIN
                        CREATE TABLE TelegramRecipients (
                            Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            ChatId NVARCHAR(100) NOT NULL,
                            DisplayName NVARCHAR(150) NOT NULL,
                            IsActive BIT NOT NULL DEFAULT 1,
                            CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
                        );

                        CREATE UNIQUE NONCLUSTERED INDEX IX_TelegramRecipients_ChatId 
                        ON TelegramRecipients (ChatId);
                    END
                ");
            }

            // One-time legacy recipient import: only perform when the table is completely empty.
            // Never overwrite manual activation or deactivation on restart (F09).
            var hasAnyRecipients = await _db.TelegramRecipients.AnyAsync();
            if (!hasAnyRecipients)
            {
                var defaultRecipients = new (string ChatId, string DisplayName)[]
                {
                    ("5600249013", "إدارة المصنع (5600249013)"),
                    ("1284402479", "إدارة المبيعات (1284402479)")
                };

                foreach (var (chatId, displayName) in defaultRecipients)
                {
                    _db.TelegramRecipients.Add(new TelegramRecipient
                    {
                        ChatId = chatId,
                        DisplayName = displayName,
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                // Also import any additional chat IDs from configuration if not already present
                var configuredIds = GetConfiguredChatIds();
                foreach (var cid in configuredIds)
                {
                    if (!defaultRecipients.Any(d => d.ChatId == cid))
                    {
                        _db.TelegramRecipients.Add(new TelegramRecipient
                        {
                            ChatId = cid,
                            DisplayName = $"المستلم {cid}",
                            IsActive = true,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }

                await _db.SaveChangesAsync();
                _logger.LogInformation("Initial Telegram recipients seeded successfully.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure Telegram tables or seed recipients (may already exist or DB provider unsupported).");
        }
    }

    public async Task<int> ProcessPendingOutboxAsync(int? specificQuoteId = null, string? suggestedPdfPath = null, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var query = _db.QuoteTelegramDeliveries
            .Where(d => (d.Status == "Pending" || (d.Status == "Processing" && d.ClaimExpiresAt != null && d.ClaimExpiresAt < now))
                     && d.Status != "Sent"
                     && (d.NextRetryAt == null || d.NextRetryAt <= now)
                     && d.RetryCount < 5);

        if (specificQuoteId.HasValue)
        {
            query = query.Where(d => d.QuoteId == specificQuoteId.Value);
        }

        var candidateIds = await query
            .OrderBy(d => d.CreatedAt)
            .Select(d => d.Id)
            .Take(25)
            .ToListAsync(cancellationToken);

        int processedCount = 0;
        foreach (var deliveryId in candidateIds)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var claimToken = Guid.NewGuid().ToString("N");
            var claimExpiresAt = DateTime.UtcNow.AddMinutes(2);

            // Atomic per-recipient job claim / lease to prevent concurrent workers from sending the same Pending delivery (F10)
            var claimed = await _db.QuoteTelegramDeliveries
                .Where(d => d.Id == deliveryId 
                         && (d.Status == "Pending" || (d.Status == "Processing" && d.ClaimExpiresAt != null && d.ClaimExpiresAt < now))
                         && d.Status != "Sent")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, "Processing")
                    .SetProperty(d => d.ClaimToken, claimToken)
                    .SetProperty(d => d.ClaimedAt, DateTime.UtcNow)
                    .SetProperty(d => d.ClaimExpiresAt, claimExpiresAt)
                    .SetProperty(d => d.LastAttemptAt, DateTime.UtcNow),
                    cancellationToken);

            if (claimed == 0)
            {
                // Already claimed or sent by another worker/process
                continue;
            }

            try
            {
                await DispatchClaimedDeliveryAsync(deliveryId, claimToken, suggestedPdfPath, cancellationToken);
                processedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error dispatching claimed delivery {DeliveryId}", deliveryId);
            }
        }

        return processedCount;
    }

    private async Task DispatchClaimedDeliveryAsync(int deliveryId, string claimToken, string? suggestedPdfPath, CancellationToken cancellationToken)
    {
        var delivery = await _db.QuoteTelegramDeliveries
            .Include(d => d.Quote)
                .ThenInclude(q => q.User)
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.ClaimToken == claimToken, cancellationToken);

        if (delivery == null || delivery.Quote == null)
        {
            return;
        }

        var quote = delivery.Quote;
        var chatId = delivery.ChatId;

        // Verify recipient is still active in DB (F09)
        var recipient = await _db.TelegramRecipients
            .FirstOrDefaultAsync(r => r.ChatId == chatId, cancellationToken);

        if (recipient == null || !recipient.IsActive)
        {
            _logger.LogWarning("Telegram recipient {ChatId} is no longer active in database. Failing delivery for quote {QuoteNumber}.", chatId, quote.QuoteNumber);
            delivery.Status = "Failed";
            delivery.ErrorMessage = "مستلم المحادثة غير مفعّل حالياً أو تم حذفه من قاعدة البيانات.";
            delivery.ClaimToken = null;
            delivery.ClaimExpiresAt = null;
            await TrySaveChangesAsync(cancellationToken);
            return;
        }

        var displayName = recipient.DisplayName;

        // Resolve employee name strictly server-side
        string employeeFullName = quote.User?.FullName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(employeeFullName) && quote.CreatedByUserId.HasValue)
        {
            var user = await _db.Users.FindAsync(new object[] { quote.CreatedByUserId.Value }, cancellationToken);
            if (user != null) employeeFullName = user.FullName;
        }
        if (string.IsNullOrWhiteSpace(employeeFullName) && quote.UserId > 0)
        {
            var user = await _db.Users.FindAsync(new object[] { quote.UserId }, cancellationToken);
            if (user != null) employeeFullName = user.FullName;
        }
        if (string.IsNullOrWhiteSpace(employeeFullName))
        {
            employeeFullName = "مسؤول المبيعات";
        }

        // Quote Date
        string quoteDateStr;
        var dateMatch = Regex.Match(quote.QuoteNumber, @"\d{6}-(\d{2}-\d{2}-\d{4})");
        if (dateMatch.Success) quoteDateStr = dateMatch.Groups[1].Value;
        else quoteDateStr = quote.CreatedAt.ToString("dd-MM-yyyy");

        var caption = $"عرض سعر جديد - EG BLOCK\n\n" +
                      $"رقم الصادر: {quote.QuoteNumber}\n" +
                      $"العميل: {quote.ClientName}\n" +
                      $"المشروع: {quote.ProjectName}\n" +
                      $"أنشأه: {employeeFullName}\n" +
                      $"التاريخ: {quoteDateStr}";

        // Resolve and validate PDF file
        var pdfPath = ResolvePdfPath(quote, suggestedPdfPath);
        bool pdfExists = !string.IsNullOrEmpty(pdfPath) && File.Exists(pdfPath);

        byte[]? fileBytes = null;
        string? pdfValidationError = null;
        string? fileSha256 = null;

        if (!pdfExists)
        {
            pdfValidationError = "ملف PDF غير موجود على الخادم للإرسال.";
        }
        else
        {
            try
            {
                fileBytes = await File.ReadAllBytesAsync(pdfPath!, cancellationToken);
                if (fileBytes == null || fileBytes.Length == 0)
                {
                    pdfValidationError = "ملف PDF فارغ (0 بايت).";
                }
                else
                {
                    var headerLength = Math.Min(fileBytes.Length, 1024);
                    var headerAscii = System.Text.Encoding.ASCII.GetString(fileBytes, 0, headerLength);
                    if (!headerAscii.Contains("%PDF-"))
                    {
                        pdfValidationError = "توقيع ملف PDF غير صالح (%PDF- غير موجود في ترويسة الملف).";
                    }
                    else
                    {
                        fileSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes));
                    }
                }
            }
            catch (Exception ex)
            {
                pdfValidationError = $"تعذر قراءة ملف PDF من القرص: {ex.Message}";
            }
        }

        if (pdfValidationError != null || fileBytes == null)
        {
            delivery.Status = "Failed";
            delivery.ErrorMessage = pdfValidationError ?? "ملف PDF غير متوفر.";
            delivery.RetryCount++;
            delivery.ClaimToken = null;
            delivery.ClaimExpiresAt = null;
            await TrySaveChangesAsync(cancellationToken);
            return;
        }

        var botToken = GetBotToken();
        if (string.IsNullOrWhiteSpace(botToken))
        {
            delivery.Status = "Failed";
            delivery.ErrorMessage = "رمز بوت التيليجرام غير مهيأ في بيئة التشغيل (Telegram__BotToken).";
            delivery.RetryCount++;
            delivery.ClaimToken = null;
            delivery.ClaimExpiresAt = null;
            await TrySaveChangesAsync(cancellationToken);
            return;
        }

        // Send via HTTP POST
        HttpResponseMessage? response = null;
        string? responseBody = null;
        bool ambiguousOutcome = false;
        string? failureReason = null;

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId), "chat_id");
            form.Add(new StringContent(caption), "caption");

            var safeAsciiFileName = $"EG-BLOCK-{quote.QuoteNumber}.pdf";
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            fileContent.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("form-data")
            {
                Name = "\"document\"",
                FileName = $"\"{safeAsciiFileName}\""
            };
            form.Add(fileContent);

            var endpoint = $"https://api.telegram.org/bot{botToken}/sendDocument";
            response = await client.PostAsync(endpoint, form, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (TaskCanceledException tce) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout while waiting for Telegram response (F10 item 9)
            ambiguousOutcome = true;
            failureReason = $"Network timeout waiting for Telegram response: {tce.Message}";
        }
        catch (HttpRequestException hre)
        {
            ambiguousOutcome = true;
            failureReason = $"Network transmission error: {hre.Message}";
        }
        catch (Exception ex)
        {
            failureReason = $"Exception sending Telegram document: {ex.Message}";
        }

        if (response != null && response.IsSuccessStatusCode)
        {
            string? messageId = ExtractMessageId(responseBody ?? "");
            delivery.Status = "Sent";
            delivery.TelegramMessageId = messageId;
            delivery.SentAt = DateTime.UtcNow;
            delivery.ErrorMessage = null;
            delivery.FileSha256 = fileSha256;
            delivery.ClaimToken = null;
            delivery.ClaimExpiresAt = null;
            await TrySaveChangesAsync(cancellationToken);

            _logger.LogInformation("Telegram document sent successfully to chat {ChatId} ({DisplayName}) for quote {QuoteNumber} (MsgId: {MsgId}).",
                chatId, displayName, quote.QuoteNumber, messageId);
        }
        else if (response != null && (int)response.StatusCode == 429)
        {
            // Telegram rate limit (HTTP 429) handling with retry_after (F10 item 7)
            int retryAfterSeconds = 15;
            try
            {
                if (!string.IsNullOrEmpty(responseBody))
                {
                    using var errDoc = JsonDocument.Parse(responseBody);
                    if (errDoc.RootElement.TryGetProperty("parameters", out var paramsElem) &&
                        paramsElem.TryGetProperty("retry_after", out var raProp))
                    {
                        retryAfterSeconds = raProp.GetInt32();
                    }
                }
                else if (response.Headers.RetryAfter?.Delta.HasValue == true)
                {
                    retryAfterSeconds = (int)Math.Ceiling(response.Headers.RetryAfter.Delta.Value.TotalSeconds);
                }
            }
            catch { }

            delivery.Status = "Pending";
            delivery.NextRetryAt = DateTime.UtcNow.AddSeconds(retryAfterSeconds);
            delivery.ErrorMessage = $"Telegram rate limit (429). Retry after {retryAfterSeconds}s.";
            delivery.RetryCount++;
            delivery.ClaimToken = null;
            delivery.ClaimExpiresAt = null;
            await TrySaveChangesAsync(cancellationToken);

            _logger.LogWarning("Telegram rate limited (429) for chat {ChatId}. Rescheduled for retry in {Seconds}s.", chatId, retryAfterSeconds);
        }
        else if (response != null && (int)response.StatusCode >= 400 && (int)response.StatusCode < 500)
        {
            // Permanent client error (400, 401, 403, 404)
            string errorDesc = ExtractErrorDescription(responseBody ?? "") ?? $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
            delivery.Status = "Failed";
            delivery.ErrorMessage = SanitizeErrorMessage(errorDesc, botToken);
            delivery.RetryCount++;
            delivery.ClaimToken = null;
            delivery.ClaimExpiresAt = null;
            await TrySaveChangesAsync(cancellationToken);

            _logger.LogWarning("Telegram sendDocument client error for chat {ChatId} on quote {QuoteNumber}: {Error}",
                chatId, quote.QuoteNumber, delivery.ErrorMessage);
        }
        else if (ambiguousOutcome || (response != null && (int)response.StatusCode >= 500))
        {
            // Ambiguous network outcome or Telegram server error (F10 item 9)
            // Mark as Unknown requiring reconciliation rather than blindly resending
            string reason = failureReason ?? $"HTTP {(int)response!.StatusCode}: {response.ReasonPhrase}";
            delivery.Status = "Unknown";
            delivery.ErrorMessage = SanitizeErrorMessage(reason, botToken);
            delivery.RetryCount++;
            delivery.ClaimToken = null;
            delivery.ClaimExpiresAt = null;
            delivery.NextRetryAt = DateTime.UtcNow.AddMinutes(5);
            await TrySaveChangesAsync(cancellationToken);

            _logger.LogWarning("Ambiguous delivery outcome for chat {ChatId} on quote {QuoteNumber}: {Reason}",
                chatId, quote.QuoteNumber, delivery.ErrorMessage);
        }
        else
        {
            // Transient error with bounded exponential backoff (F10 item 6)
            var backoffSeconds = Math.Min(15 * Math.Pow(2, delivery.RetryCount), 300);
            delivery.RetryCount++;
            delivery.ClaimToken = null;
            delivery.ClaimExpiresAt = null;

            if (delivery.RetryCount < 5)
            {
                delivery.Status = "Pending";
                delivery.NextRetryAt = DateTime.UtcNow.AddSeconds(backoffSeconds);
                delivery.ErrorMessage = SanitizeErrorMessage(failureReason ?? "Transient delivery failure", botToken);
            }
            else
            {
                delivery.Status = "Failed";
                delivery.ErrorMessage = $"Exceeded max retries (5). {SanitizeErrorMessage(failureReason, botToken)}";
            }

            await TrySaveChangesAsync(cancellationToken);
            _logger.LogWarning("Delivery attempt failed for chat {ChatId} on quote {QuoteNumber} (RetryCount: {Count}): {Reason}",
                chatId, quote.QuoteNumber, delivery.RetryCount, delivery.ErrorMessage);
        }
    }

    public async Task<List<QuoteTelegramDeliveryResult>> SendQuoteDocumentAsync(int quoteId, string? pdfPath = null, string? specificChatId = null, CancellationToken cancellationToken = default)
    {
        var quote = await _db.Quotes
            .Include(q => q.TelegramDeliveries)
            .FirstOrDefaultAsync(q => q.Id == quoteId, cancellationToken);

        if (quote == null) return new List<QuoteTelegramDeliveryResult>();

        // Ensure outbox delivery records exist for all approved active recipients
        var targets = await ResolveTargetRecipientsAsync(quote, isRetry: false, specificChatId, allowNewRecipient: false, cancellationToken);
        bool addedAny = false;
        foreach (var t in targets)
        {
            var existing = quote.TelegramDeliveries.FirstOrDefault(d => d.ChatId == t.ChatId);
            if (existing == null)
            {
                var newDel = new QuoteTelegramDelivery
                {
                    QuoteId = quote.Id,
                    QuoteNumber = quote.QuoteNumber,
                    ChatId = t.ChatId,
                    Status = "Pending",
                    RetryCount = 0,
                    NextRetryAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };
                _db.QuoteTelegramDeliveries.Add(newDel);
                quote.TelegramDeliveries.Add(newDel);
                addedAny = true;
            }
        }
        if (addedAny)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Process pending outbox
        await ProcessPendingOutboxAsync(quoteId, pdfPath, cancellationToken);

        // Build results
        return await BuildDeliveryResultsAsync(quoteId, cancellationToken);
    }

    public async Task<List<QuoteTelegramDeliveryResult>> RetryQuoteDeliveryAsync(int quoteId, string? specificChatId = null, bool allowNewRecipient = false, CancellationToken cancellationToken = default)
    {
        var quote = await _db.Quotes
            .Include(q => q.TelegramDeliveries)
            .FirstOrDefaultAsync(q => q.Id == quoteId, cancellationToken);

        if (quote == null) return new List<QuoteTelegramDeliveryResult>();

        var targets = await ResolveTargetRecipientsAsync(quote, isRetry: true, specificChatId, allowNewRecipient, cancellationToken);

        bool changesMade = false;
        foreach (var t in targets)
        {
            var del = quote.TelegramDeliveries.FirstOrDefault(d => d.ChatId == t.ChatId);
            if (del == null)
            {
                if (allowNewRecipient)
                {
                    var newDel = new QuoteTelegramDelivery
                    {
                        QuoteId = quote.Id,
                        QuoteNumber = quote.QuoteNumber,
                        ChatId = t.ChatId,
                        Status = "Pending",
                        RetryCount = 0,
                        NextRetryAt = DateTime.UtcNow,
                        CreatedAt = DateTime.UtcNow
                    };
                    _db.QuoteTelegramDeliveries.Add(newDel);
                    quote.TelegramDeliveries.Add(newDel);
                    changesMade = true;
                }
            }
            else if (del.Status != "Sent")
            {
                // Reset failed, unknown, or pending delivery for immediate retry
                del.Status = "Pending";
                del.NextRetryAt = DateTime.UtcNow;
                del.ClaimToken = null;
                del.ClaimExpiresAt = null;
                changesMade = true;
            }
            // If already Sent, do not reset! (F09 & F10)
        }

        if (changesMade)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        await ProcessPendingOutboxAsync(quoteId, null, cancellationToken);

        return await BuildDeliveryResultsAsync(quoteId, cancellationToken);
    }

    private async Task<List<QuoteTelegramDeliveryResult>> BuildDeliveryResultsAsync(int quoteId, CancellationToken cancellationToken)
    {
        var deliveries = await _db.QuoteTelegramDeliveries
            .Where(d => d.QuoteId == quoteId)
            .ToListAsync(cancellationToken);

        var recipients = await _db.TelegramRecipients
            .ToDictionaryAsync(r => r.ChatId, r => r.DisplayName, cancellationToken);

        var list = new List<QuoteTelegramDeliveryResult>();
        foreach (var d in deliveries)
        {
            recipients.TryGetValue(d.ChatId, out var displayName);
            list.Add(new QuoteTelegramDeliveryResult
            {
                ChatId = d.ChatId,
                DisplayName = displayName ?? d.ChatId,
                Success = d.Status == "Sent",
                Status = d.Status,
                MessageId = d.TelegramMessageId,
                FileSha256 = d.FileSha256,
                ErrorMessage = d.ErrorMessage,
                AlreadySent = d.Status == "Sent" && d.RetryCount == 0 && d.SentAt.HasValue
            });
        }
        return list;
    }

    public async Task<TelegramBotInfoDto> GetBotInfoAsync(CancellationToken cancellationToken = default)
    {
        var token = GetBotToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new TelegramBotInfoDto { IsConfigured = false, ErrorMessage = "رمز البوت غير مهيأ في بيئة التشغيل." };
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var response = await client.GetAsync($"https://api.telegram.org/bot{token}/getMe", cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("result", out var result))
                {
                    var username = result.TryGetProperty("username", out var u) ? u.GetString() : null;
                    var firstName = result.TryGetProperty("first_name", out var fn) ? fn.GetString() : null;
                    var id = result.TryGetProperty("id", out var idProp) ? idProp.GetInt64() : (long?)null;
                    var info = new TelegramBotInfoDto
                    {
                        IsConfigured = true,
                        BotUsername = username,
                        BotFirstName = firstName,
                        BotId = id,
                        RecentChats = new List<TelegramRecentChatDto>()
                    };

                    try
                    {
                        // Check Webhook Info (strictly read-only diagnostics; NEVER delete webhook, NEVER consume updates) (F11)
                        var whResp = await client.GetAsync($"https://api.telegram.org/bot{token}/getWebhookInfo", cancellationToken);
                        if (whResp.IsSuccessStatusCode)
                        {
                            var whJson = await whResp.Content.ReadAsStringAsync(cancellationToken);
                            using var whDoc = JsonDocument.Parse(whJson);
                            if (whDoc.RootElement.TryGetProperty("result", out var whRes))
                            {
                                if (whRes.TryGetProperty("url", out var urlProp))
                                {
                                    info.WebhookUrl = RedactSensitiveUrl(urlProp.GetString());
                                }
                                if (whRes.TryGetProperty("has_custom_certificate", out var certProp))
                                {
                                    info.HasCustomCertificate = certProp.GetBoolean();
                                }
                                if (whRes.TryGetProperty("pending_update_count", out var pendingProp))
                                {
                                    info.PendingUpdateCount = pendingProp.GetInt32();
                                }
                                if (whRes.TryGetProperty("last_error_message", out var lastErrProp))
                                {
                                    info.LastErrorMessage = SanitizeErrorMessage(lastErrProp.GetString() ?? "", token);
                                }
                                if (whRes.TryGetProperty("last_error_date", out var lastErrDateProp) && lastErrDateProp.TryGetInt64(out var unixDate))
                                {
                                    info.LastErrorDate = DateTimeOffset.FromUnixTimeSeconds(unixDate).UtcDateTime;
                                }
                            }
                        }
                        info.UpdatesStatus = "Webhook / passive mode active";
                    }
                    catch (Exception ex)
                    {
                        info.UpdatesStatus = SanitizeErrorMessage(ex.Message, token);
                        _logger.LogWarning(ex, "Failed to inspect Telegram webhook info.");
                    }

                    return info;
                }
            }
            return new TelegramBotInfoDto { IsConfigured = true, ErrorMessage = "تعذر الاتصال بـ Telegram getMe." };
        }
        catch (Exception ex)
        {
            return new TelegramBotInfoDto { IsConfigured = true, ErrorMessage = SanitizeErrorMessage(ex.Message, token) };
        }
    }

    public async Task<List<TelegramRecentChatDto>> DiscoverRecentChatsAsync(CancellationToken cancellationToken = default)
    {
        var token = GetBotToken();
        var recentChats = new List<TelegramRecentChatDto>();
        if (string.IsNullOrWhiteSpace(token))
        {
            return recentChats;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var updatesResp = await client.GetAsync($"https://api.telegram.org/bot{token}/getUpdates?offset=-50&limit=50", cancellationToken);
            if (updatesResp.IsSuccessStatusCode)
            {
                var updatesJson = await updatesResp.Content.ReadAsStringAsync(cancellationToken);
                using var updatesDoc = JsonDocument.Parse(updatesJson);
                if (updatesDoc.RootElement.TryGetProperty("result", out var updatesResult) &&
                    updatesResult.ValueKind == JsonValueKind.Array)
                {
                    var seen = new HashSet<long>();
                    foreach (var update in updatesResult.EnumerateArray())
                    {
                        JsonElement chatElem = default;
                        if (update.TryGetProperty("message", out var msg) && msg.TryGetProperty("chat", out var c1))
                        {
                            chatElem = c1;
                        }
                        else if (update.TryGetProperty("my_chat_member", out var mcm) && mcm.TryGetProperty("chat", out var c2))
                        {
                            chatElem = c2;
                        }
                        else if (update.TryGetProperty("channel_post", out var cp) && cp.TryGetProperty("chat", out var c3))
                        {
                            chatElem = c3;
                        }

                        if (chatElem.ValueKind == JsonValueKind.Object && chatElem.TryGetProperty("id", out var chatIdProp))
                        {
                            var chatId = chatIdProp.GetInt64();
                            if (seen.Add(chatId))
                            {
                                recentChats.Add(new TelegramRecentChatDto
                                {
                                    ChatId = chatId,
                                    Type = chatElem.TryGetProperty("type", out var tp) ? tp.GetString() : null,
                                    Username = chatElem.TryGetProperty("username", out var un) ? un.GetString() : null,
                                    FirstName = chatElem.TryGetProperty("first_name", out var fn2) ? fn2.GetString() : null,
                                    LastName = chatElem.TryGetProperty("last_name", out var ln) ? ln.GetString() : null
                                });
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to discover recent Telegram chats.");
        }

        return recentChats;
    }

    private static string? RedactSensitiveUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        var redacted = Regex.Replace(url, @"bot\d+:[A-Za-z0-9_-]+", "bot***");
        redacted = Regex.Replace(redacted, @"([?&](?:token|secret|key|password)=)[^&]+", "$1***", RegexOptions.IgnoreCase);
        return redacted;
    }

    private class TargetRecipientInfo
    {
        public string ChatId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    private async Task<List<TargetRecipientInfo>> ResolveTargetRecipientsAsync(
        Quote quote, 
        bool isRetry, 
        string? specificChatId, 
        bool allowNewRecipient,
        CancellationToken cancellationToken)
    {
        var targets = new List<TargetRecipientInfo>();

        if (!string.IsNullOrWhiteSpace(specificChatId))
        {
            var trimmed = specificChatId.Trim();
            var rec = await _db.TelegramRecipients
                .FirstOrDefaultAsync(r => r.ChatId == trimmed, cancellationToken);

            // Reject unregistered Chat IDs (F09)
            if (rec == null)
            {
                throw new ArgumentException("معرّف المحادثة غير مسجل في قائمة المستلمين المعتمدين.");
            }

            // Reject inactive Chat IDs (F09)
            if (!rec.IsActive)
            {
                throw new InvalidOperationException("مستلم المحادثة غير مفعّل حالياً في النظام.");
            }

            if (isRetry)
            {
                bool wasOriginalRecipient = quote.TelegramDeliveries.Any(d => d.ChatId == trimmed);
                if (!wasOriginalRecipient && !allowNewRecipient)
                {
                    throw new InvalidOperationException("لا يمكن إعادة الإرسال لمستلم لم يكن ضمن المستلمين المعتمدين الأصليين لعرض السعر بدون إذن صريح.");
                }
            }

            targets.Add(new TargetRecipientInfo
            {
                ChatId = trimmed,
                DisplayName = rec.DisplayName
            });
            return targets;
        }

        if (isRetry)
        {
            // Ordinary bulk retry: only target authorized intended recipients who had delivery records for this quote,
            // and who are CURRENTLY active in the database (F09).
            var activeRecipients = await _db.TelegramRecipients
                .Where(r => r.IsActive)
                .ToDictionaryAsync(r => r.ChatId, r => r.DisplayName, cancellationToken);

            var intendedDeliveries = quote.TelegramDeliveries
                .Where(d => activeRecipients.ContainsKey(d.ChatId))
                .ToList();

            foreach (var d in intendedDeliveries)
            {
                targets.Add(new TargetRecipientInfo
                {
                    ChatId = d.ChatId,
                    DisplayName = activeRecipients[d.ChatId]
                });
            }

            return targets;
        }
        else
        {
            // Fresh quote: send strictly to approved active recipients from the database (F09)
            var activeRecipients = await _db.TelegramRecipients
                .Where(r => r.IsActive)
                .OrderBy(r => r.Id)
                .ToListAsync(cancellationToken);

            // Zero active recipients must mean no automatic delivery targets — never silently fall back to hardcoded IDs (F09)
            if (activeRecipients.Count == 0)
            {
                _logger.LogWarning("No active Telegram recipients configured in database. Automatic Telegram delivery skipped for quote {QuoteNumber}.", quote.QuoteNumber);
                return targets; // Zero targets!
            }

            foreach (var r in activeRecipients)
            {
                targets.Add(new TargetRecipientInfo
                {
                    ChatId = r.ChatId,
                    DisplayName = r.DisplayName
                });
            }

            return targets;
        }
    }

    private async Task TrySaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving Telegram delivery record.");
        }
    }

    private string? ResolvePdfPath(Quote quote, string? suggestedPath)
    {
        if (!string.IsNullOrEmpty(suggestedPath) && File.Exists(suggestedPath))
        {
            return suggestedPath;
        }

        var appDataQuotes = Path.Combine(_env.ContentRootPath, "App_Data", "quotes");
        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var wwwrootQuotes = Path.Combine(webRoot, "quotes");

        // 1. Check quote.StorageFileName in App_Data/quotes (F15)
        if (!string.IsNullOrEmpty(quote.StorageFileName))
        {
            var candidate = Path.Combine(appDataQuotes, quote.StorageFileName);
            if (File.Exists(candidate)) return candidate;
        }

        // 2. Check filename parsed from quote.PdfUrl
        if (!string.IsNullOrEmpty(quote.PdfUrl))
        {
            try
            {
                var rawUrl = quote.PdfUrl.Split('?')[0];
                var fileName = Path.GetFileName(Uri.UnescapeDataString(rawUrl));
                
                var c1 = Path.Combine(appDataQuotes, fileName);
                if (File.Exists(c1)) return c1;

                var c2 = Path.Combine(wwwrootQuotes, fileName);
                if (File.Exists(c2)) return c2;
            }
            catch { }
        }

        // 3. Search App_Data/quotes for matching quote number
        if (Directory.Exists(appDataQuotes))
        {
            var match = Directory.GetFiles(appDataQuotes, $"*{quote.QuoteNumber}*.pdf").FirstOrDefault();
            if (!string.IsNullOrEmpty(match)) return match;
        }

        // 4. Legacy fallback: search wwwroot/quotes
        if (Directory.Exists(wwwrootQuotes))
        {
            var match = Directory.GetFiles(wwwrootQuotes, $"*{quote.QuoteNumber}*.pdf").FirstOrDefault();
            if (!string.IsNullOrEmpty(match)) return match;
        }

        return suggestedPath;
    }

    private string? GetBotToken()
    {
        var token = _configuration["Telegram:BotToken"]
                    ?? _configuration["Telegram__BotToken"]
                    ?? Environment.GetEnvironmentVariable("Telegram__BotToken")
                    ?? Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");

        if (string.IsNullOrWhiteSpace(token) || token.StartsWith("<"))
            return null;

        return token.Trim();
    }

    public List<string> GetConfiguredChatIds()
    {
        var raw = _configuration["Telegram:ChatIds"]
                  ?? _configuration["Telegram__ChatIds"]
                  ?? Environment.GetEnvironmentVariable("Telegram__ChatIds")
                  ?? "5600249013,1284402479";

        var list = raw.Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .Where(s => !string.IsNullOrEmpty(s))
                      .Distinct()
                      .ToList();

        if (list.Count == 0)
        {
            list = new List<string> { "5600249013", "1284402479" };
        }

        return list;
    }

    private static string? ExtractMessageId(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("message_id", out var msgId))
            {
                return msgId.ToString();
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractErrorDescription(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("description", out var desc))
            {
                return desc.GetString();
            }
        }
        catch { }
        return null;
    }

    public async Task<TelegramVerificationResultDto> VerifyDeliveredPdfAsync(string fileId, int quoteId, CancellationToken cancellationToken = default)
    {
        var token = GetBotToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new TelegramVerificationResultDto { Success = false, ErrorMessage = "رمز البوت غير مهيأ." };
        }

        var quote = await _db.Quotes.FindAsync(new object[] { quoteId }, cancellationToken);
        if (quote == null)
        {
            return new TelegramVerificationResultDto { Success = false, ErrorMessage = "عرض السعر غير موجود." };
        }

        var localPath = ResolvePdfPath(quote, null);
        if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath))
        {
            return new TelegramVerificationResultDto { Success = false, QuoteNumber = quote.QuoteNumber, ErrorMessage = "ملف PDF المحلي غير موجود على الخادم." };
        }

        try
        {
            var localBytes = await File.ReadAllBytesAsync(localPath, cancellationToken);
            using var sha = System.Security.Cryptography.SHA256.Create();
            var localHash = Convert.ToHexString(sha.ComputeHash(localBytes));

            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            // 1. Call getFile
            var getFileResp = await client.GetAsync($"https://api.telegram.org/bot{token}/getFile?file_id={fileId}", cancellationToken);
            var getFileJson = await getFileResp.Content.ReadAsStringAsync(cancellationToken);
            if (!getFileResp.IsSuccessStatusCode)
            {
                return new TelegramVerificationResultDto
                {
                    Success = false,
                    QuoteNumber = quote.QuoteNumber,
                    ErrorMessage = $"Telegram getFile failed: {SanitizeErrorMessage(getFileJson, token)}"
                };
            }

            using var doc = JsonDocument.Parse(getFileJson);
            if (!doc.RootElement.TryGetProperty("result", out var res) ||
                !res.TryGetProperty("file_path", out var fpProp))
            {
                return new TelegramVerificationResultDto
                {
                    Success = false,
                    QuoteNumber = quote.QuoteNumber,
                    ErrorMessage = "Telegram getFile did not return file_path."
                };
            }

            var filePath = fpProp.GetString();
            if (string.IsNullOrEmpty(filePath))
            {
                return new TelegramVerificationResultDto
                {
                    Success = false,
                    QuoteNumber = quote.QuoteNumber,
                    ErrorMessage = "Empty file_path from Telegram."
                };
            }

            // 2. Download actual file from Telegram
            var fileUrl = $"https://api.telegram.org/file/bot{token}/{filePath}";
            var fileResp = await client.GetAsync(fileUrl, cancellationToken);
            if (!fileResp.IsSuccessStatusCode)
            {
                return new TelegramVerificationResultDto
                {
                    Success = false,
                    QuoteNumber = quote.QuoteNumber,
                    ErrorMessage = $"Failed to download file from Telegram: HTTP {(int)fileResp.StatusCode}"
                };
            }

            var telegramBytes = await fileResp.Content.ReadAsByteArrayAsync(cancellationToken);
            var telegramHash = Convert.ToHexString(sha.ComputeHash(telegramBytes));

            var headerAscii = System.Text.Encoding.ASCII.GetString(telegramBytes, 0, Math.Min(telegramBytes.Length, 1024));
            bool hasValidSig = headerAscii.Contains("%PDF-");
            bool hashesMatch = string.Equals(telegramHash, localHash, StringComparison.OrdinalIgnoreCase);

            return new TelegramVerificationResultDto
            {
                Success = true,
                QuoteNumber = quote.QuoteNumber,
                DocumentFileName = Path.GetFileName(filePath),
                DocumentMimeType = "application/pdf",
                TelegramFileSize = telegramBytes.Length,
                TelegramFileSha256 = telegramHash,
                LocalFileSize = localBytes.Length,
                LocalFileSha256 = localHash,
                HashesMatch = hashesMatch,
                HasValidPdfSignature = hasValidSig
            };
        }
        catch (Exception ex)
        {
            return new TelegramVerificationResultDto
            {
                Success = false,
                QuoteNumber = quote.QuoteNumber,
                ErrorMessage = SanitizeErrorMessage(ex.Message, token)
            };
        }
    }

    private static string SanitizeErrorMessage(string message, string? token)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        if (!string.IsNullOrEmpty(token))
        {
            message = message.Replace(token, "***");
        }

        message = Regex.Replace(message, @"bot\d+:[A-Za-z0-9_-]+", "bot***");
        if (message.Length > 1900)
        {
            message = message.Substring(0, 1900) + "...";
        }
        return message;
    }
}
