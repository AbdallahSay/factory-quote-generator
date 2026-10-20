using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.Models;
using FactoryQuoteApi.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryQuoteApi.SecurityTests;

public sealed class QuoteReadTests : IAsyncLifetime
{
    private readonly SyntheticHost _host = new();
    public Task InitializeAsync() => _host.StartAsync();
    public Task DisposeAsync() => _host.DisposeAsync();

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("expired")]
    [InlineData("tampered")]
    public async Task Missing_or_invalid_session_is_401_for_existing_and_missing_ids(string? session)
    {
        var token = session switch
        {
            "expired" => _host.EmployeeToken(101, TimeSpan.FromHours(-1)),
            "tampered" => _host.EmployeeToken(101) + "tampered",
            _ => session
        };
        foreach (var id in new[] { 11, 999999 })
        {
            using var response = await Read(id, token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain("Synthetic client", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData(101, 11)]
    [InlineData(102, 12)]
    public async Task Employee_reads_own_quote_and_items(int userId, int quoteId)
    {
        using var response = await Read(quoteId, _host.EmployeeToken(userId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(quoteId, json.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("Synthetic item", json.RootElement.GetProperty("items")[0].GetProperty("productName").GetString());
        Assert.Equal(6m, json.RootElement.GetProperty("totalAmount").GetDecimal());
    }

    [Theory]
    [InlineData(101, 12)]
    [InlineData(102, 11)]
    public async Task Other_employee_and_nonexistent_quote_have_same_denial(int userId, int otherQuoteId)
    {
        var token = _host.EmployeeToken(userId);
        using var denied = await Read(otherQuoteId, token);
        using var missing = await Read(999999, token);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Equal(missing.StatusCode, denied.StatusCode);
        // Default ProblemDetails may contain a per-request traceId; semantic denial is identical.
        using var deniedJson = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        using var missingJson = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
        foreach (var key in new[] { "status", "title", "type" })
            Assert.Equal(missingJson.RootElement.GetProperty(key).ToString(), deniedJson.RootElement.GetProperty(key).ToString());
        Assert.False(deniedJson.RootElement.TryGetProperty("quoteNumber", out _));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("owner")]
    public async Task Privileged_session_reads_all_quotes_including_legacy(string role)
    {
        foreach (var id in new[] { 11, 12, 13, 14, 15 })
        {
            using var response = await Read(id, _host.RoleToken(role));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var missing = await Read(999999, _host.RoleToken(role));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData(13)] // UserId alone is not verified creator attribution.
    [InlineData(14)] // Conflicting creator fields are not reconciled by this read.
    public async Task Employee_cannot_inherit_ambiguous_legacy_ownership(int id)
    {
        using var response = await Read(id, _host.EmployeeToken(101));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await _host.WithDb(async db =>
        {
            var quote = await db.Quotes.FindAsync(id);
            Assert.Equal(101, quote!.UserId);
            Assert.Equal(id == 13 ? null : 102, quote.CreatedByUserId);
        });
    }

    [Fact]
    public async Task Employee_token_for_legacy_admin_surrogate_is_not_personal_ownership()
    {
        using var response = await Read(15, _host.EmployeeToken(1));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var another = await Read(11, _host.EmployeeToken(1));
        Assert.Equal(HttpStatusCode.NotFound, another.StatusCode); // Users.Role must not elevate token role.
    }

    [Fact]
    public async Task Without_admin_surrogate_employee_attribution_is_ambiguous_and_fails_closed()
    {
        await _host.WithDb(async db =>
        {
            db.Quotes.Remove((await db.Quotes.FindAsync(15))!);
            db.Users.Remove((await db.Users.FindAsync(1))!);
            await db.SaveChangesAsync();
        });
        using var response = await Read(11, _host.EmployeeToken(101));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var owner = await Read(11, _host.RoleToken("owner"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    [Theory]
    [InlineData("quote")]
    [InlineData("unexpected")]
    public async Task Valid_unmapped_shared_role_has_no_personal_or_global_read_rights(string role)
    {
        using var response = await Read(15, _host.RoleToken(role));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("password-reset")]
    public async Task Revoked_employee_session_cannot_read(string change)
    {
        var token = _host.EmployeeToken(101);
        await _host.WithDb(async db =>
        {
            var user = (await db.Users.FindAsync(101))!;
            if (change == "disabled") user.IsActive = false;
            else user.PasswordHash = "synthetic-replacement-hash";
            await db.SaveChangesAsync();
        });
        using var response = await Read(11, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Query_and_identity_headers_cannot_override_authenticated_employee()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/quotes/12?userId=102&createdByUserId=102&role=owner");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.EmployeeToken(101));
        request.Headers.Add("X-UserId", "102");
        request.Headers.Add("X-Role", "owner");
        using var response = await _host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("employee")]
    [InlineData("admin")]
    [InlineData("owner")]
    public async Task Response_has_only_explicit_business_fields_and_no_sensitive_graph(string role)
    {
        var token = role == "employee" ? _host.EmployeeToken(101) : _host.RoleToken(role);
        using var response = await Read(11, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        var expected = new[] { "id", "quoteNumber", "clientName", "clientEmail", "clientPhone", "contactPerson",
            "contactTitle", "projectName", "location", "issuerName", "issuerJobTitle", "issuerPrefix",
            "termsJson", "totalAmount", "pdfUrl", "docxUrl", "createdAt", "items" };
        Assert.Equal(expected.Order(), json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(new[] { "id", "productName", "size", "capacity", "quantity", "unitPrice", "lineTotal" }.Order(),
            json.RootElement.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).Order());
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "user", "passwordHash", "password", "token", "sessionSalt", "signingKey", "role",
            "auditLogs", "telegramDeliveries", "quote", "botToken", "isActive", "userId", "createdByUserId" };
        void Inspect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) { Assert.DoesNotContain(property.Name, forbidden); Inspect(property.Value); }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Inspect(item);
        }
        Inspect(json.RootElement);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(SyntheticHost.SigningKey, text);
        Assert.DoesNotContain("synthetic-password-material", text);
        Assert.DoesNotContain("synthetic-session-salt", text);
    }

    [Fact]
    public async Task Read_has_no_notification_or_history_mutation_side_effects()
    {
        await _host.WithDb(async db =>
        {
            db.TelegramRecipients.Add(new TelegramRecipient { ChatId = "synthetic-chat", DisplayName = "Independent recipient" });
            db.QuoteTelegramDeliveries.Add(new QuoteTelegramDelivery { QuoteId = 11, QuoteNumber = "SEC-01-11", ChatId = "synthetic-chat", Status = "Sent", TelegramMessageId = "synthetic-message", RetryCount = 2 });
            await db.SaveChangesAsync();
        });
        using var response = await Read(11, _host.EmployeeToken(101));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, _host.Transport.Sends);
        await _host.WithDb(async db =>
        {
            Assert.Equal(5, await db.Quotes.CountAsync());
            Assert.Equal(0, await db.AuditLogs.CountAsync());
            var delivery = await db.QuoteTelegramDeliveries.SingleAsync();
            Assert.Equal("Sent", delivery.Status);
            Assert.Equal(2, delivery.RetryCount);
            Assert.Equal("synthetic-message", delivery.TelegramMessageId);
            Assert.True((await db.TelegramRecipients.SingleAsync()).IsActive);
        });
    }

    [Fact]
    public async Task Existing_generation_still_dispatches_pdf_with_authenticated_creator_to_independent_recipient()
    {
        await _host.WithDb(async db =>
        {
            db.TelegramRecipients.Add(new TelegramRecipient { ChatId = "synthetic-chat", DisplayName = "Independent recipient" });
            await db.SaveChangesAsync();
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes/generate")
        {
            Content = JsonContent.Create(new { userId = 102, clientName = "Synthetic SEC-01", projectName = "Regression",
                items = new[] { new { productName = "Synthetic item", quantity = 2, unitPrice = 3 } } })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.EmployeeToken(101));
        using var response = await _host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = result.GetProperty("quoteId").GetInt32();
        Assert.Equal(1, _host.Transport.Sends);
        Assert.Contains("Synthetic Employee A", _host.Transport.LastBody);
        Assert.Contains("synthetic-chat", _host.Transport.LastBody);
        Assert.Contains("application/pdf", _host.Transport.LastBody);
        await _host.WithDb(async db =>
        {
            var quote = (await db.Quotes.FindAsync(id))!;
            Assert.Equal(101, quote.UserId);
            Assert.Equal(101, quote.CreatedByUserId);
            Assert.Equal(101, (await db.AuditLogs.SingleAsync()).UserId);
            var delivery = await db.QuoteTelegramDeliveries.SingleAsync();
            Assert.Equal("Sent", delivery.Status);
            using var scope = _host.App.Services.CreateScope();
            var retry = await scope.ServiceProvider.GetRequiredService<ITelegramDeliveryService>().RetryQuoteDeliveryAsync(id);
            Assert.True(Assert.Single(retry).AlreadySent);
            Assert.Equal(1, _host.Transport.Sends);
        });
        using var read = await Read(id, _host.EmployeeToken(101));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    private async Task<HttpResponseMessage> Read(int id, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/quotes/{id}");
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await _host.Client.SendAsync(request);
    }
}
