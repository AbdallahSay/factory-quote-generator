using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.DTOs;
using FactoryQuoteApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FactoryQuoteApi.SecurityTests;

public sealed class OfficialEmployeeAuthenticationTests : IAsyncLifetime
{
    private readonly SyntheticHost _host = new();
    public Task InitializeAsync() => _host.StartAsync();
    public Task DisposeAsync() => _host.DisposeAsync();

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }

    [Fact]
    public async Task Shared_Quote_Login_Is_Rejected_With_401()
    {
        var loginPayload = new
        {
            role = "quote",
            password = "AnyQuotePassword@2026"
        };

        using var req = CreateRequest(HttpMethod.Post, "/api/auth/login", token: null, body: loginPayload);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        Assert.Contains("تم إلغاء كلمة المرور المشتركة", json);
    }

    [Theory]
    [InlineData("/api/quotes/generate")]
    [InlineData("/api/quotes/preview")]
    [InlineData("/api/admin/audit")]
    public async Task Legacy_Shared_Quote_Token_Is_Rejected(string endpoint)
    {
        var legacyQuoteToken = _host.RoleToken("quote");
        object body = endpoint.Contains("audit")
            ? new { action = "Test", entityId = "1" }
            : new QuoteRequestDto
            {
                ClientName = "Test Client",
                Items = new List<ProductItemDto>
                {
                    new() { ProductName = "Item A", Quantity = 1, UnitPrice = 100 }
                }
            };

        using var req = CreateRequest(HttpMethod.Post, endpoint, token: legacyQuoteToken, body: body);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/quotes/generate")]
    [InlineData("/api/quotes/preview")]
    [InlineData("/api/admin/audit")]
    public async Task Anonymous_Access_To_Quotation_Endpoints_Is_Rejected(string endpoint)
    {
        object body = endpoint.Contains("audit")
            ? new { action = "Test", entityId = "1" }
            : new QuoteRequestDto
            {
                ClientName = "Test Client",
                Items = new List<ProductItemDto>
                {
                    new() { ProductName = "Item A", Quantity = 1, UnitPrice = 100 }
                }
            };

        using var req = CreateRequest(HttpMethod.Post, endpoint, token: null, body: body);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Disabled_Employee_Login_Is_Rejected()
    {
        // 1. Create a disabled user in the DB with known password hash
        string password = "SecretEmployeePassword@123";
        string email = "disabled_emp@example.invalid";

        await _host.WithDb(async db =>
        {
            var securityService = _host.App.Services.GetRequiredService<Services.ISecurityService>();
            db.Users.Add(new User
            {
                Id = 205,
                FullName = "Disabled Employee",
                Email = email,
                Role = "Sales",
                PasswordHash = securityService.HashPassword(password),
                IsActive = false
            });
            await db.SaveChangesAsync();
        });

        // 2. Attempt login
        var loginPayload = new
        {
            email = email,
            password = password
        };

        using var req = CreateRequest(HttpMethod.Post, "/api/auth/login", token: null, body: loginPayload);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        Assert.Contains("هذا الحساب معطل", json);
    }

    [Fact]
    public async Task Active_Employee_Login_Succeeds_With_Token()
    {
        string password = "SecretEmployeePassword@123";
        string email = "active_emp@example.invalid";

        await _host.WithDb(async db =>
        {
            var securityService = _host.App.Services.GetRequiredService<Services.ISecurityService>();
            db.Users.Add(new User
            {
                Id = 206,
                FullName = "Active Employee",
                Email = email,
                Role = "Sales",
                PasswordHash = securityService.HashPassword(password),
                IsActive = true
            });
            await db.SaveChangesAsync();
        });

        var loginPayload = new
        {
            email = email,
            password = password
        };

        using var req = CreateRequest(HttpMethod.Post, "/api/auth/login", token: null, body: loginPayload);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("token").GetString()));
    }

    [Fact]
    public async Task Newly_Generated_Quote_Strictly_Attributes_Authenticated_Employee()
    {
        var employeeToken = _host.EmployeeToken(101);
        var requestPayload = new QuoteRequestDto
        {
            ClientName = "Security Batch 02 Test Client",
            ProjectName = "Attribution Verification",
            Items = new List<ProductItemDto>
            {
                new() { ProductName = "Item 1", Quantity = 5, UnitPrice = 200 }
            }
        };

        using var req = CreateRequest(HttpMethod.Post, "/api/quotes/generate", token: employeeToken, body: requestPayload);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        int quoteId = doc.RootElement.GetProperty("quoteId").GetInt32();

        await _host.WithDb(async db =>
        {
            var savedQuote = await db.Quotes.FindAsync(quoteId);
            Assert.NotNull(savedQuote);
            Assert.Equal(101, savedQuote.UserId);
            Assert.Equal(101, savedQuote.CreatedByUserId);
        });
    }

    [Fact]
    public async Task Audit_Endpoint_Binds_Actor_To_Authenticated_Session()
    {
        var employeeToken = _host.EmployeeToken(101);
        var spoofedAuditPayload = new
        {
            userId = 9999, // Attempted spoof
            action = "SecurityBatch02TestAction",
            entityId = "TEST-ENT-001"
        };

        using var req = CreateRequest(HttpMethod.Post, "/api/admin/audit", token: employeeToken, body: spoofedAuditPayload);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await _host.WithDb(async db =>
        {
            var log = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "SecurityBatch02TestAction");

            Assert.NotNull(log);
            Assert.Equal(101, log.UserId); // Bound to authenticated session, not the spoofed 9999
        });
    }

    [Fact]
    public async Task Owner_Retains_Administrative_Access_While_Admin_Denied_EmployeeManagement()
    {
        var ownerToken = _host.RoleToken("owner");
        var adminToken = _host.RoleToken("admin");

        // Owner can access employees
        using var ownerReq = CreateRequest(HttpMethod.Get, "/api/admin/employees", token: ownerToken);
        using var ownerRes = await _host.Client.SendAsync(ownerReq);
        Assert.Equal(HttpStatusCode.OK, ownerRes.StatusCode);

        // Admin is DENIED access to employees (F05 Non-Regression)
        using var adminReq = CreateRequest(HttpMethod.Get, "/api/admin/employees", token: adminToken);
        using var adminRes = await _host.Client.SendAsync(adminReq);
        Assert.Equal(HttpStatusCode.Unauthorized, adminRes.StatusCode);
    }
}
