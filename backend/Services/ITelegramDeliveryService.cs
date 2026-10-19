using FactoryQuoteApi.Models;

namespace FactoryQuoteApi.Services;

public class QuoteTelegramDeliveryResult
{
    public string ChatId { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty; // Sent, Failed, Skipped
    public string? MessageId { get; set; }
    public string? DocumentFileName { get; set; }
    public string? DocumentMimeType { get; set; }
    public long? DocumentFileSize { get; set; }
    public string? DocumentFileId { get; set; }
    public string? FileSha256 { get; set; }
    public string? ErrorMessage { get; set; }
    public bool AlreadySent { get; set; }
}

public class TelegramVerificationResultDto
{
    public bool Success { get; set; }
    public string? QuoteNumber { get; set; }
    public string? DocumentFileName { get; set; }
    public string? DocumentMimeType { get; set; }
    public long? TelegramFileSize { get; set; }
    public string? TelegramFileSha256 { get; set; }
    public long? LocalFileSize { get; set; }
    public string? LocalFileSha256 { get; set; }
    public bool HashesMatch { get; set; }
    public bool HasValidPdfSignature { get; set; }
    public string? ErrorMessage { get; set; }
}

public class TelegramRecentChatDto
{
    public long ChatId { get; set; }
    public string? Type { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
}

public class TelegramBotInfoDto
{
    public bool IsConfigured { get; set; }
    public string? BotUsername { get; set; }
    public string? BotFirstName { get; set; }
    public long? BotId { get; set; }
    public string? ErrorMessage { get; set; }
    public string? WebhookUrl { get; set; }
    public string? UpdatesStatus { get; set; }
    public List<TelegramRecentChatDto> RecentChats { get; set; } = new();
}

public interface ITelegramDeliveryService
{
    Task EnsureInitializedAsync();
    Task<List<QuoteTelegramDeliveryResult>> SendQuoteDocumentAsync(int quoteId, string? pdfPath = null, string? specificChatId = null, CancellationToken cancellationToken = default);
    Task<List<QuoteTelegramDeliveryResult>> RetryQuoteDeliveryAsync(int quoteId, string? specificChatId = null, CancellationToken cancellationToken = default);
    Task<TelegramBotInfoDto> GetBotInfoAsync(CancellationToken cancellationToken = default);
    Task<TelegramVerificationResultDto> VerifyDeliveredPdfAsync(string fileId, int quoteId, CancellationToken cancellationToken = default);
}
