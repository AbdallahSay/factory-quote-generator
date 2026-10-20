using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryQuoteApi.DTOs;
using FactoryQuoteApi.Models;
using Xunit;

namespace FactoryQuoteApi.SecurityTests;

public sealed class EmployeeManagementSecurityTests : IAsyncLifetime
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

    private static object? GetPayloadForPath(string method, string path)
    {
        if (method == "GET" || method == "DELETE") return null;
        if (path.EndsWith("/reset-password"))
        {
            return new ResetEmployeePasswordRequestDto
            {
                NewPassword = "NewPassword123",
                ConfirmPassword = "NewPassword123"
            };
        }
        if (path.EndsWith("/toggle-status"))
        {
            return new ToggleActiveRequestDto
            {
                IsActive = false
            };
        }
        if (method == "PUT")
        {
            return new UpdateEmployeeRequestDto
            {
                FullName = "Updated Name",
                Email = "updated@example.invalid"
            };
        }
        return new CreateEmployeeRequestDto
        {
            FullName = "New Employee",
            Email = "new-emp@example.invalid",
            Password = "Password123",
            ConfirmPassword = "Password123"
        };
    }

    [Theory]
    [InlineData("GET", "/api/admin/employees")]
    [InlineData("POST", "/api/admin/employees")]
    [InlineData("PUT", "/api/admin/employees/101")]
    [InlineData("POST", "/api/admin/employees/101/reset-password")]
    [InlineData("POST", "/api/admin/employees/101/toggle-status")]
    [InlineData("DELETE", "/api/admin/employees/101")]
    public async Task Anonymous_cannot_access_employee_endpoints(string method, string path)
    {
        using var req = CreateRequest(new HttpMethod(method), path, token: null, body: GetPayloadForPath(method, path));
        using var res = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/admin/employees")]
    [InlineData("POST", "/api/admin/employees")]
    [InlineData("PUT", "/api/admin/employees/101")]
    [InlineData("POST", "/api/admin/employees/101/reset-password")]
    [InlineData("POST", "/api/admin/employees/101/toggle-status")]
    [InlineData("DELETE", "/api/admin/employees/101")]
    public async Task Employee_token_cannot_access_employee_endpoints(string method, string path)
    {
        var employeeToken = _host.EmployeeToken(101);
        using var req = CreateRequest(new HttpMethod(method), path, token: employeeToken, body: GetPayloadForPath(method, path));
        using var res = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/admin/employees")]
    [InlineData("POST", "/api/admin/employees")]
    [InlineData("PUT", "/api/admin/employees/101")]
    [InlineData("POST", "/api/admin/employees/101/reset-password")]
    [InlineData("POST", "/api/admin/employees/101/toggle-status")]
    [InlineData("DELETE", "/api/admin/employees/101")]
    public async Task Admin_token_cannot_access_employee_endpoints(string method, string path)
    {
        var adminToken = _host.RoleToken("admin");
        using var req = CreateRequest(new HttpMethod(method), path, token: adminToken, body: GetPayloadForPath(method, path));
        using var res = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Owner_can_manage_employees_full_lifecycle()
    {
        var ownerToken = _host.RoleToken("owner");

        // 1. Owner can list employees
        using (var listReq = CreateRequest(HttpMethod.Get, "/api/admin/employees", ownerToken))
        using (var listRes = await _host.Client.SendAsync(listReq))
        {
            Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
            var employees = await listRes.Content.ReadFromJsonAsync<List<EmployeeResponseDto>>();
            Assert.NotNull(employees);
            Assert.Contains(employees, e => e.Id == 101);
        }

        // 2. Owner can create employee
        int createdId;
        using (var createReq = CreateRequest(HttpMethod.Post, "/api/admin/employees", ownerToken, new CreateEmployeeRequestDto
        {
            FullName = "Owner Created User",
            Email = "owner-created@example.invalid",
            Password = "SecurePassword123",
            ConfirmPassword = "SecurePassword123"
        }))
        using (var createRes = await _host.Client.SendAsync(createReq))
        {
            Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
            using var json = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
            createdId = json.RootElement.GetProperty("user").GetProperty("id").GetInt32();
            Assert.True(createdId > 0);
        }

        // 3. Owner can update employee
        using (var updateReq = CreateRequest(HttpMethod.Put, $"/api/admin/employees/{createdId}", ownerToken, new UpdateEmployeeRequestDto
        {
            FullName = "Owner Updated User",
            Email = "owner-updated@example.invalid"
        }))
        using (var updateRes = await _host.Client.SendAsync(updateReq))
        {
            Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
            using var json = JsonDocument.Parse(await updateRes.Content.ReadAsStringAsync());
            Assert.Equal("Owner Updated User", json.RootElement.GetProperty("user").GetProperty("fullName").GetString());
        }

        // 4. Owner can reset employee password
        using (var resetReq = CreateRequest(HttpMethod.Post, $"/api/admin/employees/{createdId}/reset-password", ownerToken, new ResetEmployeePasswordRequestDto
        {
            NewPassword = "NewSecurePassword456",
            ConfirmPassword = "NewSecurePassword456"
        }))
        using (var resetRes = await _host.Client.SendAsync(resetReq))
        {
            Assert.Equal(HttpStatusCode.OK, resetRes.StatusCode);
        }

        // 5. Owner can toggle employee active status
        using (var toggleReq = CreateRequest(HttpMethod.Post, $"/api/admin/employees/{createdId}/toggle-status", ownerToken, new ToggleActiveRequestDto
        {
            IsActive = false
        }))
        using (var toggleRes = await _host.Client.SendAsync(toggleReq))
        {
            Assert.Equal(HttpStatusCode.OK, toggleRes.StatusCode);
            using var json = JsonDocument.Parse(await toggleRes.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.GetProperty("isActive").GetBoolean());
        }

        // 6. Owner can delete employee who has no quotes
        using (var delReq = CreateRequest(HttpMethod.Delete, $"/api/admin/employees/{createdId}", ownerToken))
        using (var delRes = await _host.Client.SendAsync(delReq))
        {
            Assert.Equal(HttpStatusCode.OK, delRes.StatusCode);
        }

        // 7. Owner cannot delete employee who has quotes (historical integrity preserved)
        using (var delWithQuotesReq = CreateRequest(HttpMethod.Delete, "/api/admin/employees/101", ownerToken))
        using (var delWithQuotesRes = await _host.Client.SendAsync(delWithQuotesReq))
        {
            Assert.Equal(HttpStatusCode.BadRequest, delWithQuotesRes.StatusCode);
            var content = await delWithQuotesRes.Content.ReadAsStringAsync();
            Assert.Contains("لا يمكن حذف هذا الحساب", content);
        }
    }
}
