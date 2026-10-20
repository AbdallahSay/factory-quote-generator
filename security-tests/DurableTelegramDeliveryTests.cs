using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FactoryQuoteApi.DTOs;
using FactoryQuoteApi.Models;
using FactoryQuoteApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryQuoteApi.SecurityTests;

public class DurableTelegramDeliveryTests : IAsyncLifetime
{
    private readonly SyntheticHost _host = new();

    public async Task InitializeAsync()
    {
        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri, string? token = null, object? body = null)
    {
        var req = new HttpRequestMessage(method, uri);
        if (!string.IsNullOrEmpty(token))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }
        if (body != null)
        {
            req.Content = JsonContent.Create(body);
        }
        return req;
    }

    private async Task EnsurePdfFileExistsAsync(Quote quote)
    {
        var appDataQuotes = Path.Combine(_host.RootPath, "App_Data", "quotes");
        Directory.CreateDirectory(appDataQuotes);
        var filename = quote.StorageFileName ?? $"{quote.QuoteNumber}.pdf";
        var path = Path.Combine(appDataQuotes, filename);
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, "%PDF- synthetic test fixture document");
        }
    }

    [Fact]
    public async Task Quote_Creation_Persists_Deliveries_In_Transactional_Outbox()
    {
        // F10 Item 2 & 3: Persistent transactional outbox records intended approved recipients
        await _host.WithDb(async db =>
        {
            db.TelegramRecipients.Add(new TelegramRecipient
            {
                ChatId = "5544332211",
                DisplayName = "Transactional Outbox Target",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });

        var employeeToken = _host.EmployeeToken(101);
        var quotePayload = new QuoteRequestDto
        {
            ClientName = "Outbox Client",
            ProjectName = "Outbox Project",
            Items = new List<ProductItemDto>
            {
                new() { ProductName = "Item 1", Quantity = 10, UnitPrice = 100 }
            }
        };

        using var req = CreateRequest(HttpMethod.Post, "/api/quotes/generate", employeeToken, quotePayload);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await _host.WithDb(async db =>
        {
            var quote = await db.Quotes
                .Include(q => q.TelegramDeliveries)
                .OrderByDescending(q => q.Id)
                .FirstOrDefaultAsync(q => q.ClientName == "Outbox Client");

            Assert.NotNull(quote);
            Assert.NotEmpty(quote.TelegramDeliveries);

            var delivery = quote.TelegramDeliveries.FirstOrDefault(d => d.ChatId == "5544332211");
            Assert.NotNull(delivery);
            // Delivery was persisted in the same transaction as Quote (either Sent immediately or Pending)
            Assert.Contains(delivery.Status, new[] { "Sent", "Pending" });
            Assert.NotNull(delivery.FileSha256);
            Assert.Equal(quote.PdfSha256, delivery.FileSha256);
        });
    }

    [Fact]
    public async Task ProcessPendingOutbox_Dispatches_Delivery_And_Marks_Sent()
    {
        // F10 Item 1 & 10: Automatic delivery preserves PDF bytes and marks Sent
        int quoteId = 11;
        await _host.WithDb(async db =>
        {
            var q = await db.Quotes.FindAsync(quoteId);
            Assert.NotNull(q);
            await EnsurePdfFileExistsAsync(q);

            db.TelegramRecipients.Add(new TelegramRecipient
            {
                ChatId = "1122334455",
                DisplayName = "Delivery Target Chat",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });

            db.QuoteTelegramDeliveries.Add(new QuoteTelegramDelivery
            {
                QuoteId = quoteId,
                QuoteNumber = q.QuoteNumber,
                ChatId = "1122334455",
                Status = "Pending",
                RetryCount = 0,
                NextRetryAt = DateTime.UtcNow.AddMinutes(-1),
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        });

        using var scope = _host.App.Services.CreateScope();
        var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramDeliveryService>();

        int processed = await telegramService.ProcessPendingOutboxAsync(quoteId);
        Assert.True(processed > 0);

        await _host.WithDb(async db =>
        {
            var delivery = await db.QuoteTelegramDeliveries
                .FirstOrDefaultAsync(d => d.QuoteId == quoteId && d.ChatId == "1122334455");

            Assert.NotNull(delivery);
            Assert.Equal("Sent", delivery.Status);
            Assert.Equal("123", delivery.TelegramMessageId);
            Assert.NotNull(delivery.SentAt);
            Assert.Null(delivery.ErrorMessage);
            Assert.Null(delivery.ClaimToken);
        });
    }

    [Fact]
    public async Task Already_Sent_Deliveries_Are_Never_Resent()
    {
        // F10 Item 8: Never automatically repeat an already-Sent delivery
        int quoteId = 11;
        await _host.WithDb(async db =>
        {
            var q = await db.Quotes.FindAsync(quoteId);
            Assert.NotNull(q);
            await EnsurePdfFileExistsAsync(q);

            var existing = await db.QuoteTelegramDeliveries
                .FirstOrDefaultAsync(d => d.QuoteId == quoteId && d.ChatId == "999000111");

            if (existing == null)
            {
                db.TelegramRecipients.Add(new TelegramRecipient
                {
                    ChatId = "999000111",
                    DisplayName = "Already Sent Target",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });

                db.QuoteTelegramDeliveries.Add(new QuoteTelegramDelivery
                {
                    QuoteId = quoteId,
                    QuoteNumber = q.QuoteNumber,
                    ChatId = "999000111",
                    Status = "Sent",
                    TelegramMessageId = "orig-msg-123",
                    SentAt = DateTime.UtcNow.AddDays(-1),
                    RetryCount = 0,
                    CreatedAt = DateTime.UtcNow.AddDays(-1)
                });

                await db.SaveChangesAsync();
            }
        });

        int sendsBefore = _host.Transport.Sends;

        using var scope = _host.App.Services.CreateScope();
        var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramDeliveryService>();

        // Attempting outbox processing on already Sent delivery
        int processed = await telegramService.ProcessPendingOutboxAsync(quoteId);
        Assert.Equal(0, processed);
        Assert.Equal(sendsBefore, _host.Transport.Sends); // No HTTP send occurred!

        // Attempting manual retry on already Sent delivery suppresses send and reports AlreadySent = true
        var retryResults = await telegramService.RetryQuoteDeliveryAsync(quoteId, "999000111");
        var res = retryResults.FirstOrDefault(r => r.ChatId == "999000111");

        Assert.NotNull(res);
        Assert.True(res.AlreadySent);
        Assert.Equal("Sent", res.Status);
        Assert.Equal(sendsBefore, _host.Transport.Sends); // Zero duplicate sends!
    }

    [Fact]
    public async Task Telegram_Rate_Limit_429_Reschedules_With_Backoff()
    {
        // F10 Item 7: Handle Telegram HTTP 429 using retry_after
        int quoteId = 12;
        await _host.WithDb(async db =>
        {
            var q = await db.Quotes.FindAsync(quoteId);
            Assert.NotNull(q);
            await EnsurePdfFileExistsAsync(q);

            db.TelegramRecipients.Add(new TelegramRecipient
            {
                ChatId = "429-rate-limited-chat",
                DisplayName = "Rate Limited Chat",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });

            db.QuoteTelegramDeliveries.Add(new QuoteTelegramDelivery
            {
                QuoteId = quoteId,
                QuoteNumber = q.QuoteNumber,
                ChatId = "429-rate-limited-chat",
                Status = "Pending",
                RetryCount = 0,
                NextRetryAt = DateTime.UtcNow.AddSeconds(-5),
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        });

        // Configure transport to simulate Telegram HTTP 429 with retry_after = 25
        _host.Transport.CustomSendDocumentHandler = _ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests: retry after 25\",\"parameters\":{\"retry_after\":25}}")
            };
            return resp;
        };

        using var scope = _host.App.Services.CreateScope();
        var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramDeliveryService>();

        await telegramService.ProcessPendingOutboxAsync(quoteId);

        await _host.WithDb(async db =>
        {
            var delivery = await db.QuoteTelegramDeliveries
                .FirstOrDefaultAsync(d => d.QuoteId == quoteId && d.ChatId == "429-rate-limited-chat");

            Assert.NotNull(delivery);
            // Delivery must NOT be marked Failed! It remains Pending with scheduled NextRetryAt
            Assert.Equal("Pending", delivery.Status);
            Assert.NotNull(delivery.NextRetryAt);
            Assert.True(delivery.NextRetryAt.Value > DateTime.UtcNow.AddSeconds(15));
            Assert.Contains("429", delivery.ErrorMessage);
            Assert.Equal(1, delivery.RetryCount);
            Assert.Null(delivery.ClaimToken);
        });

        _host.Transport.CustomSendDocumentHandler = null;
    }

    [Fact]
    public async Task Ambiguous_Network_Outcome_Marked_As_Unknown()
    {
        // F10 Item 9: Handle ambiguous network outcomes as Unknown requiring reconciliation
        int quoteId = 13;
        await _host.WithDb(async db =>
        {
            var q = await db.Quotes.FindAsync(quoteId);
            Assert.NotNull(q);
            await EnsurePdfFileExistsAsync(q);

            db.TelegramRecipients.Add(new TelegramRecipient
            {
                ChatId = "timeout-chat-id",
                DisplayName = "Timeout Chat",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });

            db.QuoteTelegramDeliveries.Add(new QuoteTelegramDelivery
            {
                QuoteId = quoteId,
                QuoteNumber = q.QuoteNumber,
                ChatId = "timeout-chat-id",
                Status = "Pending",
                RetryCount = 0,
                NextRetryAt = DateTime.UtcNow.AddSeconds(-5),
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        });

        // Simulate network timeout waiting for Telegram response
        _host.Transport.CustomSendDocumentHandler = _ => throw new TaskCanceledException("Operation timed out waiting for Telegram.");

        using var scope = _host.App.Services.CreateScope();
        var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramDeliveryService>();

        await telegramService.ProcessPendingOutboxAsync(quoteId);

        await _host.WithDb(async db =>
        {
            var delivery = await db.QuoteTelegramDeliveries
                .FirstOrDefaultAsync(d => d.QuoteId == quoteId && d.ChatId == "timeout-chat-id");

            Assert.NotNull(delivery);
            // Must be marked Unknown to prevent blind resends of documents that Telegram may have posted
            Assert.Equal("Unknown", delivery.Status);
            Assert.Contains("timeout", delivery.ErrorMessage?.ToLowerInvariant() ?? "");
            Assert.Null(delivery.ClaimToken);
        });

        _host.Transport.CustomSendDocumentHandler = null;
    }

    [Fact]
    public async Task Permanent_Client_Error_Marked_As_Failed()
    {
        // F10: Permanent client errors (HTTP 400, 403) marked Failed
        int quoteId = 14;
        await _host.WithDb(async db =>
        {
            var q = await db.Quotes.FindAsync(quoteId);
            Assert.NotNull(q);
            await EnsurePdfFileExistsAsync(q);

            db.TelegramRecipients.Add(new TelegramRecipient
            {
                ChatId = "blocked-chat-id",
                DisplayName = "Blocked Bot Chat",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });

            db.QuoteTelegramDeliveries.Add(new QuoteTelegramDelivery
            {
                QuoteId = quoteId,
                QuoteNumber = q.QuoteNumber,
                ChatId = "blocked-chat-id",
                Status = "Pending",
                RetryCount = 0,
                NextRetryAt = DateTime.UtcNow.AddSeconds(-5),
                CreatedAt = DateTime.UtcNow
            });

            await db.SaveChangesAsync();
        });

        // Simulate Telegram 403 Forbidden: bot was blocked by the user
        _host.Transport.CustomSendDocumentHandler = _ =>
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("{\"ok\":false,\"error_code\":403,\"description\":\"Forbidden: bot was blocked by the user\"}")
            };
        };

        using var scope = _host.App.Services.CreateScope();
        var telegramService = scope.ServiceProvider.GetRequiredService<ITelegramDeliveryService>();

        await telegramService.ProcessPendingOutboxAsync(quoteId);

        await _host.WithDb(async db =>
        {
            var delivery = await db.QuoteTelegramDeliveries
                .FirstOrDefaultAsync(d => d.QuoteId == quoteId && d.ChatId == "blocked-chat-id");

            Assert.NotNull(delivery);
            Assert.Equal("Failed", delivery.Status);
            Assert.Contains("blocked", delivery.ErrorMessage?.ToLowerInvariant() ?? "");
            Assert.Null(delivery.ClaimToken);
        });

        _host.Transport.CustomSendDocumentHandler = null;
    }

    [Fact]
    public async Task Read_Only_Telegram_Diagnostics_Never_Deletes_Webhook_Or_Calls_GetUpdates()
    {
        // F11 Item 1, 2, 3, 4, 7: GET /api/admin/telegram-info is completely passive and read-only
        var adminToken = _host.RoleToken("admin");
        _host.Transport.WebhookUrl = "https://example.com/webhook?token=secret123";

        using var req = CreateRequest(HttpMethod.Get, "/api/admin/telegram-info", adminToken);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var info = await res.Content.ReadFromJsonAsync<TelegramBotInfoDto>();
        Assert.NotNull(info);
        Assert.True(info.IsConfigured);
        Assert.Equal("synthetic_bot", info.BotUsername);

        // Never deleted webhook
        Assert.False(_host.Transport.DeleteWebhookCalled, "deleteWebhook must never be called during diagnostics!");

        // Diagnostics must not consume updates or call getUpdates passively
        Assert.False(_host.Transport.GetUpdatesCalled, "getUpdates must not be called during read-only diagnostics!");

        // Sensitive tokens in webhook URL must be redacted
        Assert.DoesNotContain("secret123", info.WebhookUrl ?? "");
        Assert.Contains("***", info.WebhookUrl ?? "");

        _host.Transport.WebhookUrl = "https://example.com/webhook";
    }

    [Fact]
    public async Task Explicit_Owner_Discover_Updates_Is_Audited_And_Requires_Owner()
    {
        // F11 Item 6: Explicit Owner-authorized update discovery operation
        var employeeToken = _host.EmployeeToken(101);
        var adminToken = _host.RoleToken("admin");
        var ownerToken = _host.RoleToken("owner");

        // 1. Employee cannot discover updates
        using var empReq = CreateRequest(HttpMethod.Post, "/api/admin/telegram/discover-updates", employeeToken);
        using var empRes = await _host.Client.SendAsync(empReq);
        Assert.Equal(HttpStatusCode.Unauthorized, empRes.StatusCode);

        // 2. Admin cannot discover updates (Owner-only)
        using var adminReq = CreateRequest(HttpMethod.Post, "/api/admin/telegram/discover-updates", adminToken);
        using var adminRes = await _host.Client.SendAsync(adminReq);
        Assert.Equal(HttpStatusCode.Unauthorized, adminRes.StatusCode);

        // 3. Owner can discover updates
        using var ownerReq = CreateRequest(HttpMethod.Post, "/api/admin/telegram/discover-updates", ownerToken);
        using var ownerRes = await _host.Client.SendAsync(ownerReq);
        Assert.Equal(HttpStatusCode.OK, ownerRes.StatusCode);

        Assert.True(_host.Transport.GetUpdatesCalled);

        // Verify authoritative audit log was created (F08)
        await _host.WithDb(async db =>
        {
            var audit = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "TelegramUpdatesDiscovered");

            Assert.NotNull(audit);
            Assert.Contains("DiscoveredCount=1", audit.EntityId);
        });
    }
}
