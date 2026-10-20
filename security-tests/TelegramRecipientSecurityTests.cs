using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryQuoteApi.DTOs;
using FactoryQuoteApi.Models;
using FactoryQuoteApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryQuoteApi.SecurityTests;

public class TelegramRecipientSecurityTests : IAsyncLifetime
{
    private readonly SyntheticHost _host = new();

    public Task InitializeAsync() => _host.StartAsync();
    public Task DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Zero_active_recipients_means_zero_delivery_targets_without_fallback()
    {
        // Deactivate all recipients in the database
        await _host.WithDb(async db =>
        {
            var all = await db.TelegramRecipients.ToListAsync();
            foreach (var r in all) r.IsActive = false;
            await db.SaveChangesAsync();
        });

        int initialSends = _host.Transport.Sends;

        // Generate a new quote
        var quotePayload = new QuoteRequestDto
        {
            ClientName = "شركة الفيروز",
            ProjectName = "مشروع الإسكان",
            Items = new List<ProductItemDto> { new() { ProductName = "صنف أ", Quantity = 10, UnitPrice = 100 } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes/generate")
        {
            Content = JsonContent.Create(quotePayload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.EmployeeToken(101));
        using var response = await _host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert no Telegram deliveries were sent (zero fallback to hardcoded IDs) (F09)
        Assert.Equal(initialSends, _host.Transport.Sends);

        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var quoteId = result.GetProperty("quoteId").GetInt32();

        await _host.WithDb(async db =>
        {
            var deliveries = await db.QuoteTelegramDeliveries.Where(d => d.QuoteId == quoteId).ToListAsync();
            Assert.Empty(deliveries);
        });
    }

    [Fact]
    public async Task Retry_rejects_unregistered_chat_id()
    {
        var quoteId = await CreateQuoteAndSeedDelivery("999999", "Failed");

        // Attempt retry targeting an unregistered chat ID
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/quotes/{quoteId}/retry-telegram?chatId=unregistered_random_chat");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.RoleToken("admin"));
        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("غير مسجل", body);
    }

    [Fact]
    public async Task Retry_rejects_inactive_chat_id()
    {
        // Add an inactive recipient
        const string inactiveChatId = "9876543210";
        await _host.WithDb(async db =>
        {
            db.TelegramRecipients.Add(new TelegramRecipient
            {
                ChatId = inactiveChatId,
                DisplayName = "مستلم غير مفعل",
                IsActive = false,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });

        var quoteId = await CreateQuoteAndSeedDelivery(inactiveChatId, "Failed");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/quotes/{quoteId}/retry-telegram?chatId={inactiveChatId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.RoleToken("admin"));
        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("غير مفعّل", body);
    }

    [Fact]
    public async Task Retry_rejects_new_unintended_recipient_without_explicit_authorization()
    {
        // Add an active recipient that was NOT an original recipient of this quote
        const string newChatId = "1122334455";
        await _host.WithDb(async db =>
        {
            db.TelegramRecipients.Add(new TelegramRecipient
            {
                ChatId = newChatId,
                DisplayName = "مستلم جديد مضاف حديثاً",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });

        // Quote was originally sent to "5600249013", never to "1122334455"
        var quoteId = await CreateQuoteAndSeedDelivery("5600249013", "Sent");

        // Attempt retry to new recipient without allowNewRecipient
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/quotes/{quoteId}/retry-telegram?chatId={newChatId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.RoleToken("admin"));
        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("بدون إذن صريح", body);
    }

    [Fact]
    public async Task Retry_with_explicit_authorization_allows_new_recipient_and_records_audit_log()
    {
        const string newChatId = "5566778899";
        await _host.WithDb(async db =>
        {
            db.TelegramRecipients.Add(new TelegramRecipient
            {
                ChatId = newChatId,
                DisplayName = "مستلم معتمد جديد",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });

        var quoteId = await CreateQuoteAndSeedDelivery("5600249013", "Sent");

        // Retry with allowNewRecipient=true
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/quotes/{quoteId}/retry-telegram?chatId={newChatId}&allowNewRecipient=true");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.RoleToken("admin"));
        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify audit log was recorded (F09)
        await _host.WithDb(async db =>
        {
            var audit = await db.AuditLogs
                .FirstOrDefaultAsync(a => a.Action == "HistoricalQuoteDispatchedToNewRecipient" && a.EntityId != null && a.EntityId.Contains(newChatId));
            Assert.NotNull(audit);
        });
    }

    [Fact]
    public async Task Sent_retry_is_suppressed_and_returns_already_sent()
    {
        const string chatId = "5600249013";
        await _host.WithDb(async db =>
        {
            var existing = await db.TelegramRecipients.FirstOrDefaultAsync(r => r.ChatId == chatId);
            if (existing == null)
            {
                db.TelegramRecipients.Add(new TelegramRecipient { ChatId = chatId, DisplayName = "مستلم معتمد", IsActive = true, CreatedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
        });

        var quoteId = await CreateQuoteAndSeedDelivery(chatId, "Sent");
        int sendsBefore = _host.Transport.Sends;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/quotes/{quoteId}/retry-telegram?chatId={chatId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.RoleToken("admin"));
        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Outbound send should NOT have occurred
        Assert.Equal(sendsBefore, _host.Transport.Sends);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var results = json.GetProperty("results").EnumerateArray().ToList();
        Assert.Single(results);
        Assert.True(results[0].GetProperty("alreadySent").GetBoolean());
    }

    [Fact]
    public async Task GetBotInfoAsync_never_calls_deleteWebhook()
    {
        using var scope = _host.App.Services.CreateScope();
        var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramDeliveryService>();

        var botInfo = await telegramService.GetBotInfoAsync();

        Assert.True(botInfo.IsConfigured);
        Assert.Equal("synthetic_bot", botInfo.BotUsername);
        Assert.Equal("https://example.com/webhook", botInfo.WebhookUrl);

        // Crucial F09 requirement: Webhook must NEVER be silently deleted
        Assert.False(_host.Transport.DeleteWebhookCalled);
    }

    private async Task<int> CreateQuoteAndSeedDelivery(string chatId, string deliveryStatus)
    {
        int quoteId = 0;
        await _host.WithDb(async db =>
        {
            var quote = new Quote
            {
                QuoteNumber = "TG-SEC-" + Guid.NewGuid().ToString("N")[..8],
                UserId = 101,
                CreatedByUserId = 101,
                ClientName = "شركة تجريبية",
                ProjectName = "مشروع تجريبي",
                TotalAmount = 500,
                PdfUrl = "https://example.com/quotes/test.pdf",
                DocxUrl = "https://example.com/quotes/test.docx",
                CreatedAt = DateTime.UtcNow
            };
            db.Quotes.Add(quote);
            await db.SaveChangesAsync();

            quoteId = quote.Id;

            db.QuoteTelegramDeliveries.Add(new QuoteTelegramDelivery
            {
                QuoteId = quote.Id,
                QuoteNumber = quote.QuoteNumber,
                ChatId = chatId,
                Status = deliveryStatus,
                TelegramMessageId = deliveryStatus == "Sent" ? "msg-12345" : null,
                SentAt = deliveryStatus == "Sent" ? DateTime.UtcNow : null,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });

        return quoteId;
    }
}
