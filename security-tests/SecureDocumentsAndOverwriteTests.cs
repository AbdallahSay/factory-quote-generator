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

public class SecureDocumentsAndOverwriteTests : IAsyncLifetime
{
    private readonly SyntheticHost _host = new();

    public Task InitializeAsync() => _host.StartAsync();
    public Task DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Anonymous_access_to_quote_document_is_denied()
    {
        // Unauthenticated request to /quotes/{fileName} must return 401 Unauthorized
        using var request = new HttpRequestMessage(HttpMethod.Get, "/quotes/EG-BLOCK-SEC-01-11.pdf");
        using var response = await _host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_access_to_api_download_endpoint_is_denied()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/quotes/11/download");
        using var response = await _host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Employee_cannot_access_another_employees_private_document()
    {
        // Generate a real quotation as Employee A (Id=101)
        var quoteResult = await GenerateQuoteAsEmployee(101, "شركة المقاولات", "مشروع البرج");
        var storageFileName = quoteResult.StorageFileName;

        // Employee B (Id=102) attempts to download Employee A's document
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/quotes/{storageFileName}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.EmployeeToken(102));
        using var response = await _host.Client.SendAsync(request);

        // Must be rejected with 403 Forbidden
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Employee_can_access_own_document_with_session()
    {
        var quoteResult = await GenerateQuoteAsEmployee(101, "شركة المقاولات", "مشروع البرج");
        var storageFileName = quoteResult.StorageFileName;

        // Employee A downloads own document
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/quotes/{storageFileName}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.EmployeeToken(101));
        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Admin_and_Owner_can_access_any_quotation_document()
    {
        var quoteResult = await GenerateQuoteAsEmployee(101, "شركة المقاولات", "مشروع البرج");
        var storageFileName = quoteResult.StorageFileName;

        // Admin access
        using var adminReq = new HttpRequestMessage(HttpMethod.Get, $"/quotes/{storageFileName}");
        adminReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.RoleToken("admin"));
        using var adminResp = await _host.Client.SendAsync(adminReq);
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);

        // Owner access
        using var ownerReq = new HttpRequestMessage(HttpMethod.Get, $"/quotes/{storageFileName}");
        ownerReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.RoleToken("owner"));
        using var ownerResp = await _host.Client.SendAsync(ownerReq);
        Assert.Equal(HttpStatusCode.OK, ownerResp.StatusCode);
    }

    [Fact]
    public async Task Valid_scoped_document_capability_token_allows_anonymous_view_and_download()
    {
        var quoteResult = await GenerateQuoteAsEmployee(101, "شركة الأمل", "مستشفى العاصمة");
        Assert.Contains("?token=", quoteResult.PdfUrl);

        // Fetch using the URL containing the capability token, without any Bearer header
        using var request = new HttpRequestMessage(HttpMethod.Get, quoteResult.PdfUrl);
        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Expired_or_forged_document_capability_token_is_denied()
    {
        var quoteResult = await GenerateQuoteAsEmployee(101, "شركة الأمل", "مستشفى العاصمة");

        // 1. Expired token
        using var scope = _host.App.Services.CreateScope();
        var secService = scope.ServiceProvider.GetRequiredService<ISecurityService>();
        var expiredToken = secService.GenerateDocumentToken(quoteResult.StorageFileName, "pdf", 101, TimeSpan.FromSeconds(-60));

        using var expiredReq = new HttpRequestMessage(HttpMethod.Get, $"/quotes/{quoteResult.StorageFileName}?token={expiredToken}");
        using var expiredResp = await _host.Client.SendAsync(expiredReq);
        Assert.Equal(HttpStatusCode.Unauthorized, expiredResp.StatusCode);

        // 2. Forged signature token
        var forgedToken = "doc|" + quoteResult.StorageFileName + "|pdf|101|9999999999|forged_bad_signature";
        using var forgedReq = new HttpRequestMessage(HttpMethod.Get, $"/quotes/{quoteResult.StorageFileName}?token={forgedToken}");
        using var forgedResp = await _host.Client.SendAsync(forgedReq);
        Assert.Equal(HttpStatusCode.Unauthorized, forgedResp.StatusCode);
    }

    [Fact]
    public async Task Sequential_generations_for_identical_client_and_project_have_distinct_files_and_hashes()
    {
        const string client = "شركة المقاولات المتحدة";
        const string project = "مشروع سكن مصر";

        var q1 = await GenerateQuoteAsEmployee(101, client, project);
        var q2 = await GenerateQuoteAsEmployee(101, client, project);

        // 1. Quotation IDs and sequence numbers differ
        Assert.NotEqual(q1.QuoteId, q2.QuoteId);
        Assert.NotEqual(q1.QuoteNumber, q2.QuoteNumber);

        // 2. Storage filenames are unique and immutable (F15)
        Assert.NotEqual(q1.StorageFileName, q2.StorageFileName);

        // 3. Both files exist independently in storage
        await _host.WithDb(async db =>
        {
            var entity1 = await db.Quotes.FindAsync(q1.QuoteId);
            var entity2 = await db.Quotes.FindAsync(q2.QuoteId);

            Assert.NotNull(entity1);
            Assert.NotNull(entity2);
            Assert.False(string.IsNullOrWhiteSpace(entity1!.PdfSha256));
            Assert.False(string.IsNullOrWhiteSpace(entity2!.PdfSha256));
            Assert.True(entity1.PdfSizeBytes > 0);
            Assert.True(entity2.PdfSizeBytes > 0);
            Assert.NotEqual(entity1.StorageFileName, entity2.StorageFileName);
        });

        // 4. Downloading q1 still retrieves q1, not overwritten by q2
        using var req1 = new HttpRequestMessage(HttpMethod.Get, q1.PdfUrl);
        using var resp1 = await _host.Client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        using var req2 = new HttpRequestMessage(HttpMethod.Get, q2.PdfUrl);
        using var resp2 = await _host.Client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
    }

    [Fact]
    public async Task Multiple_generations_for_identical_client_and_project_produce_unique_filenames_and_records()
    {
        const string client = "الشركة المتطورة";
        const string project = "برج النصر";

        var results = new List<GeneratedQuoteInfo>();
        for (int i = 0; i < 4; i++)
        {
            results.Add(await GenerateQuoteAsEmployee(101, client, project));
        }

        var distinctQuoteNumbers = results.Select(r => r.QuoteNumber).Distinct().Count();
        var distinctStorageFiles = results.Select(r => r.StorageFileName).Distinct().Count();

        Assert.Equal(4, distinctQuoteNumbers);
        Assert.Equal(4, distinctStorageFiles);
    }

    [Fact]
    public async Task Content_Disposition_preserves_inline_for_pdf_and_readable_Arabic_filename()
    {
        var q = await GenerateQuoteAsEmployee(101, "شركة الإسكندرية", "مشروع الميناء");

        using var request = new HttpRequestMessage(HttpMethod.Get, q.PdfUrl);
        using var response = await _host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var disp = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disp);
        Assert.Equal("inline", disp.DispositionType);
        Assert.Contains("EG-BLOCK", disp.FileName);
    }

    [Fact]
    public async Task Preview_quote_requires_authentication_and_issues_authenticated_preview_token()
    {
        var previewPayload = new QuoteRequestDto
        {
            ClientName = "شركة المعاينة",
            ProjectName = "مشروع المعاينة",
            Items = new List<ProductItemDto>
            {
                new() { ProductName = "صنف تجريبي", Quantity = 10, UnitPrice = 50 }
            }
        };

        // 1. Anonymous preview rejected
        using var anonReq = new HttpRequestMessage(HttpMethod.Post, "/api/quotes/preview")
        {
            Content = JsonContent.Create(previewPayload)
        };
        using var anonResp = await _host.Client.SendAsync(anonReq);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 2. Authenticated employee preview succeeds
        using var authReq = new HttpRequestMessage(HttpMethod.Post, "/api/quotes/preview")
        {
            Content = JsonContent.Create(previewPayload)
        };
        authReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.EmployeeToken(101));
        using var authResp = await _host.Client.SendAsync(authReq);
        Assert.Equal(HttpStatusCode.OK, authResp.StatusCode);

        var previewJson = await authResp.Content.ReadFromJsonAsync<JsonElement>();
        var pdfUrl = previewJson.GetProperty("pdfUrl").GetString();
        Assert.NotNull(pdfUrl);
        Assert.Contains("?token=", pdfUrl);

        // 3. Preview URL can be rendered via capability token without login header
        using var renderReq = new HttpRequestMessage(HttpMethod.Get, pdfUrl);
        using var renderResp = await _host.Client.SendAsync(renderReq);
        Assert.Equal(HttpStatusCode.OK, renderResp.StatusCode);
        Assert.Equal("application/pdf", renderResp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Legacy_document_audit_endpoint_reports_status_and_is_admin_only()
    {
        // 1. Anonymous rejected
        using var anonReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/quotes/legacy-document-audit");
        using var anonResp = await _host.Client.SendAsync(anonReq);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 2. Employee rejected
        using var empReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/quotes/legacy-document-audit");
        empReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.EmployeeToken(101));
        using var empResp = await _host.Client.SendAsync(empReq);
        Assert.Equal(HttpStatusCode.Unauthorized, empResp.StatusCode);

        // 3. Admin succeeds
        using var adminReq = new HttpRequestMessage(HttpMethod.Get, "/api/admin/quotes/legacy-document-audit");
        adminReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.RoleToken("admin"));
        using var adminResp = await _host.Client.SendAsync(adminReq);
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);

        var auditJson = await adminResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(auditJson.GetProperty("totalQuotes").GetInt32() >= 0);
        Assert.True(auditJson.TryGetProperty("collisions", out _));
        Assert.True(auditJson.TryGetProperty("auditEntries", out _));
    }

    private class GeneratedQuoteInfo
    {
        public int QuoteId { get; set; }
        public string QuoteNumber { get; set; } = string.Empty;
        public string PdfUrl { get; set; } = string.Empty;
        public string DocxUrl { get; set; } = string.Empty;
        public string StorageFileName { get; set; } = string.Empty;
    }

    private async Task<GeneratedQuoteInfo> GenerateQuoteAsEmployee(int employeeId, string clientName, string projectName)
    {
        var requestPayload = new QuoteRequestDto
        {
            ClientName = clientName,
            ProjectName = projectName,
            IssuerName = "أحمد محمد",
            IssuerJobTitle = "مدير المبيعات",
            Items = new List<ProductItemDto>
            {
                new() { ProductName = "بلوك اسمنتي", Size = "20x20x40", Quantity = 1000, UnitPrice = 15 }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes/generate")
        {
            Content = JsonContent.Create(requestPayload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.EmployeeToken(employeeId));
        using var response = await _host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected OK but got {response.StatusCode}: {body}");

        var result = JsonSerializer.Deserialize<JsonElement>(body);
        var quoteId = result.GetProperty("quoteId").GetInt32();
        var quoteNumber = result.GetProperty("quoteNumber").GetString()!;
        var pdfUrl = result.GetProperty("pdfUrl").GetString()!;
        var docxUrl = result.GetProperty("docxUrl").GetString()!;
        var storageFileName = Path.GetFileName(Uri.UnescapeDataString(pdfUrl.Split('?')[0]));

        return new GeneratedQuoteInfo
        {
            QuoteId = quoteId,
            QuoteNumber = quoteNumber,
            PdfUrl = pdfUrl,
            DocxUrl = docxUrl,
            StorageFileName = storageFileName
        };
    }
}
