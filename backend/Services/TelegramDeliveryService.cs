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
                        CONSTRAINT FK_QuoteTelegramDeliveries_Quotes_QuoteId FOREIGN KEY (QuoteId) REFERENCES Quotes(Id) ON DELETE CASCADE
                    );

                    CREATE UNIQUE NONCLUSTERED INDEX IX_QuoteTelegramDeliveries_QuoteId_ChatId 
                    ON QuoteTelegramDeliveries (QuoteId, ChatId);
                END
            ");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure QuoteTelegramDeliveries table (may already exist or DB provider unsupported).");
        }
    }

    public async Task<List<QuoteTelegramDeliveryResult>> SendQuoteDocumentAsync(int quoteId, string? pdfPath = null, CancellationToken cancellationToken = default)
    {
        return await ProcessDeliveriesInternalAsync(quoteId, pdfPath, isRetry: false, cancellationToken);
    }

    public async Task<List<QuoteTelegramDeliveryResult>> RetryQuoteDeliveryAsync(int quoteId, CancellationToken cancellationToken = default)
    {
        return await ProcessDeliveriesInternalAsync(quoteId, null, isRetry: true, cancellationToken);
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
                        BotId = id
                    };

                    try
                    {
                        // Check Webhook Info
                        var whResp = await client.GetAsync($"https://api.telegram.org/bot{token}/getWebhookInfo", cancellationToken);
                        if (whResp.IsSuccessStatusCode)
                        {
                            var whJson = await whResp.Content.ReadAsStringAsync(cancellationToken);
                            using var whDoc = JsonDocument.Parse(whJson);
                            if (whDoc.RootElement.TryGetProperty("result", out var whRes))
                            {
                                if (whRes.TryGetProperty("url", out var urlProp))
                                {
                                    info.WebhookUrl = urlProp.GetString();
                                    if (!string.IsNullOrEmpty(info.WebhookUrl))
                                    {
                                        // Delete webhook so bot can poll updates
                                        await client.PostAsync($"https://api.telegram.org/bot{token}/deleteWebhook", null, cancellationToken);
                                    }
                                }
                            }
                        }

                        // Retrieve last 50 updates using negative offset
                        var updatesResp = await client.GetAsync($"https://api.telegram.org/bot{token}/getUpdates?offset=-50&limit=50", cancellationToken);
                        info.UpdatesStatus = $"HTTP {(int)updatesResp.StatusCode}";
                        if (updatesResp.IsSuccessStatusCode)
                        {
                            var updatesJson = await updatesResp.Content.ReadAsStringAsync(cancellationToken);
                            using var updatesDoc = JsonDocument.Parse(updatesJson);
                            if (updatesDoc.RootElement.TryGetProperty("result", out var updatesResult) &&
                                updatesResult.ValueKind == JsonValueKind.Array)
                            {
                                int count = 0;
                                var seen = new HashSet<long>();
                                foreach (var update in updatesResult.EnumerateArray())
                                {
                                    count++;
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
                                            info.RecentChats.Add(new TelegramRecentChatDto
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
                                info.UpdatesStatus = $"OK ({count} updates, {seen.Count} distinct chats)";
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        info.UpdatesStatus = SanitizeErrorMessage(ex.Message, token);
                        _logger.LogWarning(ex, "Failed to inspect Telegram updates.");
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

    private async Task<List<QuoteTelegramDeliveryResult>> ProcessDeliveriesInternalAsync(int quoteId, string? pdfPath, bool isRetry, CancellationToken cancellationToken)
    {
        var results = new List<QuoteTelegramDeliveryResult>();

        var quote = await _db.Quotes
            .Include(q => q.User)
            .Include(q => q.TelegramDeliveries)
            .FirstOrDefaultAsync(q => q.Id == quoteId, cancellationToken);

        if (quote == null)
        {
            _logger.LogWarning("Quote ID {QuoteId} not found for Telegram delivery.", quoteId);
            return results;
        }

        // 1. Resolve employee identity strictly server-side
        string employeeFullName = quote.User?.FullName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(employeeFullName) && quote.CreatedByUserId.HasValue)
        {
            var user = await _db.Users.FindAsync(new object[] { quote.CreatedByUserId.Value }, cancellationToken);
            if (user != null)
            {
                employeeFullName = user.FullName;
            }
        }
        if (string.IsNullOrWhiteSpace(employeeFullName) && quote.UserId > 0)
        {
            var user = await _db.Users.FindAsync(new object[] { quote.UserId }, cancellationToken);
            if (user != null)
            {
                employeeFullName = user.FullName;
            }
        }
        if (string.IsNullOrWhiteSpace(employeeFullName))
        {
            employeeFullName = "مسؤول المبيعات";
        }

        // 2. Resolve Quote Date
        string quoteDateStr;
        var dateMatch = Regex.Match(quote.QuoteNumber, @"\d{6}-(\d{2}-\d{2}-\d{4})");
        if (dateMatch.Success)
        {
            quoteDateStr = dateMatch.Groups[1].Value;
        }
        else
        {
            quoteDateStr = quote.CreatedAt.ToString("dd-MM-yyyy");
        }

        // 3. Format Caption exactly as requested
        var caption = $"عرض سعر جديد - EG BLOCK\n\n" +
                      $"رقم الصادر: {quote.QuoteNumber}\n" +
                      $"العميل: {quote.ClientName}\n" +
                      $"المشروع: {quote.ProjectName}\n" +
                      $"أنشأه: {employeeFullName}\n" +
                      $"التاريخ: {quoteDateStr}";

        // 4. Resolve and validate PDF file on disk
        pdfPath = ResolvePdfPath(quote, pdfPath);
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
                    // Verify PDF signature %PDF-
                    var headerLength = Math.Min(fileBytes.Length, 1024);
                    var headerAscii = System.Text.Encoding.ASCII.GetString(fileBytes, 0, headerLength);
                    if (!headerAscii.Contains("%PDF-"))
                    {
                        pdfValidationError = "توقيع ملف PDF غير صالح (%PDF- غير موجود في ترويسة الملف).";
                    }
                    else
                    {
                        fileSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fileBytes));
                        _logger.LogInformation("Quotation PDF verified for quote {QuoteNumber}: path={Path}, size={Size} bytes, SHA256={Hash}",
                            quote.QuoteNumber, pdfPath, fileBytes.Length, fileSha256);
                    }
                }
            }
            catch (Exception ex)
            {
                pdfValidationError = $"تعذر قراءة ملف PDF من القرص: {ex.Message}";
            }
        }

        // 5. Get Bot Token & Recipients
        var botToken = GetBotToken();
        var chatIds = GetRecipientChatIds();

        foreach (var chatId in chatIds)
        {
            // Find existing delivery record for idempotency
            var delivery = quote.TelegramDeliveries.FirstOrDefault(d => d.ChatId == chatId);
            if (delivery == null)
            {
                delivery = await _db.QuoteTelegramDeliveries
                    .FirstOrDefaultAsync(d => d.QuoteId == quote.Id && d.ChatId == chatId, cancellationToken);
            }

            // If already sent, do not duplicate!
            if (delivery != null && delivery.Status == "Sent")
            {
                _logger.LogInformation("Quote {QuoteNumber} already delivered to Telegram chat {ChatId} (MsgId: {MsgId}). Skipping to prevent duplicates.",
                    quote.QuoteNumber, chatId, delivery.TelegramMessageId);

                results.Add(new QuoteTelegramDeliveryResult
                {
                    ChatId = chatId,
                    Success = true,
                    AlreadySent = true,
                    Status = "Sent",
                    MessageId = delivery.TelegramMessageId,
                    FileSha256 = fileSha256
                });
                continue;
            }

            // Create record if not exists
            if (delivery == null)
            {
                delivery = new QuoteTelegramDelivery
                {
                    QuoteId = quote.Id,
                    QuoteNumber = quote.QuoteNumber,
                    ChatId = chatId,
                    Status = "Pending",
                    CreatedAt = DateTime.UtcNow
                };
                _db.QuoteTelegramDeliveries.Add(delivery);
            }

            // Check if PDF file is valid
            if (pdfValidationError != null || fileBytes == null)
            {
                delivery.Status = "Failed";
                delivery.ErrorMessage = pdfValidationError ?? "ملف PDF غير متوفر.";
                delivery.RetryCount++;

                results.Add(new QuoteTelegramDeliveryResult
                {
                    ChatId = chatId,
                    Success = false,
                    Status = "Failed",
                    ErrorMessage = delivery.ErrorMessage,
                    FileSha256 = fileSha256
                });
                continue;
            }

            // Check if Bot Token is configured
            if (string.IsNullOrWhiteSpace(botToken))
            {
                _logger.LogWarning("Telegram bot token is not configured (Telegram__BotToken). Skipping delivery to {ChatId} for quote {QuoteNumber}.",
                    chatId, quote.QuoteNumber);

                delivery.Status = "Failed";
                delivery.ErrorMessage = "رمز بوت التيليجرام غير مهيأ في بيئة التشغيل (Telegram__BotToken).";
                delivery.RetryCount++;

                results.Add(new QuoteTelegramDeliveryResult
                {
                    ChatId = chatId,
                    Success = false,
                    Status = "Failed",
                    ErrorMessage = delivery.ErrorMessage,
                    FileSha256 = fileSha256
                });
                continue;
            }

            // Send via Telegram Bot API sendDocument
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

                _logger.LogInformation("Sending Telegram PDF document ({FileName}, {Bytes} bytes, SHA256: {Hash}) to chat {ChatId} for quote {QuoteNumber}...",
                    safeAsciiFileName, fileBytes.Length, fileSha256, chatId, quote.QuoteNumber);

                // Never log the raw token in log output or error strings
                var endpoint = $"https://api.telegram.org/bot{botToken}/sendDocument";
                var response = await client.PostAsync(endpoint, form, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    string? messageId = ExtractMessageId(responseBody);
                    var (tgFileName, tgMime, tgSize, tgFileId) = ExtractDocumentInfo(responseBody);

                    delivery.Status = "Sent";
                    delivery.TelegramMessageId = messageId;
                    delivery.SentAt = DateTime.UtcNow;
                    delivery.ErrorMessage = null;

                    _logger.LogInformation("Telegram document sent successfully to chat {ChatId} for quote {QuoteNumber} (MsgId: {MsgId}, File: {File}, Mime: {Mime}, Size: {Size}, FileId: {FileId}).",
                        chatId, quote.QuoteNumber, messageId, tgFileName, tgMime, tgSize, tgFileId);

                    results.Add(new QuoteTelegramDeliveryResult
                    {
                        ChatId = chatId,
                        Success = true,
                        Status = "Sent",
                        MessageId = messageId,
                        DocumentFileName = tgFileName ?? safeAsciiFileName,
                        DocumentMimeType = tgMime ?? "application/pdf",
                        DocumentFileSize = tgSize ?? fileBytes.Length,
                        DocumentFileId = tgFileId,
                        FileSha256 = fileSha256
                    });
                }
                else
                {
                    string errorDesc = ExtractErrorDescription(responseBody) ?? $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}";
                    delivery.Status = "Failed";
                    delivery.ErrorMessage = SanitizeErrorMessage(errorDesc, botToken);
                    delivery.RetryCount++;

                    _logger.LogWarning("Telegram sendDocument failed for chat {ChatId} on quote {QuoteNumber}: {Error}",
                        chatId, quote.QuoteNumber, delivery.ErrorMessage);

                    results.Add(new QuoteTelegramDeliveryResult
                    {
                        ChatId = chatId,
                        Success = false,
                        Status = "Failed",
                        ErrorMessage = delivery.ErrorMessage,
                        FileSha256 = fileSha256
                    });
                }
            }
            catch (Exception ex)
            {
                var sanitized = SanitizeErrorMessage(ex.Message, botToken);
                delivery.Status = "Failed";
                delivery.ErrorMessage = sanitized;
                delivery.RetryCount++;

                _logger.LogError(ex, "Exception while sending Telegram document to chat {ChatId} for quote {QuoteNumber}.",
                    chatId, quote.QuoteNumber);

                results.Add(new QuoteTelegramDeliveryResult
                {
                    ChatId = chatId,
                    Success = false,
                    Status = "Failed",
                    ErrorMessage = sanitized,
                    FileSha256 = fileSha256
                });
            }
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving Telegram delivery statuses for quote {QuoteNumber}.", quote.QuoteNumber);
        }

        return results;
    }

    private string? ResolvePdfPath(Quote quote, string? suggestedPath)
    {
        if (!string.IsNullOrEmpty(suggestedPath) && File.Exists(suggestedPath))
        {
            return suggestedPath;
        }

        var webRoot = _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot");
        var quotesDir = Path.Combine(webRoot, "quotes");

        if (!string.IsNullOrEmpty(quote.PdfUrl))
        {
            try
            {
                var uri = new Uri(quote.PdfUrl);
                var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
                var candidate = Path.Combine(quotesDir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        if (Directory.Exists(quotesDir))
        {
            var match = Directory.GetFiles(quotesDir, $"*{quote.QuoteNumber}*.pdf").FirstOrDefault();
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

    private List<string> GetRecipientChatIds()
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

    private static (string? fileName, string? mimeType, long? fileSize, string? fileId) ExtractDocumentInfo(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (doc.RootElement.TryGetProperty("result", out var result) &&
                result.TryGetProperty("document", out var docElem))
            {
                string? fileName = docElem.TryGetProperty("file_name", out var fn) ? fn.GetString() : null;
                string? mimeType = docElem.TryGetProperty("mime_type", out var mt) ? mt.GetString() : null;
                long? fileSize = docElem.TryGetProperty("file_size", out var fs) ? fs.GetInt64() : null;
                string? fileId = docElem.TryGetProperty("file_id", out var fid) ? fid.GetString() : null;
                return (fileName, mimeType, fileSize, fileId);
            }
        }
        catch { }
        return (null, null, null, null);
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
