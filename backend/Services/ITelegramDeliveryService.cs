using FactoryQuoteApi.Models;

namespace FactoryQuoteApi.Services;

public class QuoteTelegramDeliveryResult
{
    public string ChatId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Status { get; set; } = string.Empty; // Sent, Failed, Skipped
    public string? MessageId { get; set; }
    public string? ErrorMessage { get; set; }
    public bool AlreadySent { get; set; }
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
    public List<TelegramRecentChatDto> RecentChats { get; set; } = new();
}

public interface ITelegramDeliveryService
{
    Task EnsureInitializedAsync();
    Task<List<QuoteTelegramDeliveryResult>> SendQuoteDocumentAsync(int quoteId, string? pdfPath = null, CancellationToken cancellationToken = default);
    Task<List<QuoteTelegramDeliveryResult>> RetryQuoteDeliveryAsync(int quoteId, CancellationToken cancellationToken = default);
    Task<TelegramBotInfoDto> GetBotInfoAsync(CancellationToken cancellationToken = default);
}
