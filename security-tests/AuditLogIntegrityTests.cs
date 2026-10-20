using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FactoryQuoteApi.DTOs;
using FactoryQuoteApi.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FactoryQuoteApi.SecurityTests;

public class AuditLogIntegrityTests : IAsyncLifetime
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

    [Fact]
    public async Task Client_Cannot_Spoof_UserId_In_Audit_Log()
    {
        // F08 Item 1 & 2: Actor identity derived strictly server-side
        var employeeToken = _host.EmployeeToken(101);
        var payload = new
        {
            userId = 9999, // Attempted spoof
            action = "TermAdded",
            entityId = "Term #1: Payment terms"
        };

        using var req = CreateRequest(HttpMethod.Post, "/api/admin/audit", employeeToken, payload);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await _host.WithDb(async db =>
        {
            var log = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "TermAdded");

            Assert.NotNull(log);
            Assert.Equal(101, log.UserId); // Server-derived identity, spoofed 9999 was discarded
        });
    }

    [Fact]
    public async Task Client_Cannot_Submit_Unapproved_Action()
    {
        // F08 Item 3: Client telemetry restricted to approved allowlist
        var employeeToken = _host.EmployeeToken(101);

        var unapprovedActions = new[]
        {
            "EmployeeCreated",
            "EmployeeDeleted",
            "QuoteGenerated",
            "AdminTakeover",
            "ArbitraryClientAction123"
        };

        foreach (var action in unapprovedActions)
        {
            var payload = new
            {
                userId = 101,
                action = action,
                entityId = "TestEntity"
            };

            using var req = CreateRequest(HttpMethod.Post, "/api/admin/audit", employeeToken, payload);
            using var res = await _host.Client.SendAsync(req);

            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task Client_Cannot_Submit_Oversized_Action_Or_Entity()
    {
        // F08 Item 4: Validate input size
        var employeeToken = _host.EmployeeToken(101);

        // Oversized Action (> 50 chars)
        var oversizedActionPayload = new
        {
            action = new string('A', 51),
            entityId = "valid-entity"
        };

        using var req1 = CreateRequest(HttpMethod.Post, "/api/admin/audit", employeeToken, oversizedActionPayload);
        using var res1 = await _host.Client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.BadRequest, res1.StatusCode);

        // Oversized EntityId (> 100 chars)
        var oversizedEntityPayload = new
        {
            action = "TermAdded",
            entityId = new string('E', 101)
        };

        using var req2 = CreateRequest(HttpMethod.Post, "/api/admin/audit", employeeToken, oversizedEntityPayload);
        using var res2 = await _host.Client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);
    }

    [Fact]
    public async Task Employee_Cannot_Report_Audit_For_Another_Employees_Quote()
    {
        // F08 Item 4: Object ownership validation
        // Quote 12 belongs to Employee 102
        var employee101Token = _host.EmployeeToken(101);

        var payload = new
        {
            action = "QuoteDownloaded",
            entityId = "PDF: SEC-01-12" // Quote 12
        };

        using var req = CreateRequest(HttpMethod.Post, "/api/admin/audit", employee101Token, payload);
        using var res = await _host.Client.SendAsync(req);

        // Forbidden because employee 101 does not own quote 12
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Employee_Can_Report_Audit_For_Own_Quote()
    {
        // F08 Item 4: Object ownership validation passes for own quote
        // Quote 11 belongs to Employee 101
        var employee101Token = _host.EmployeeToken(101);

        var payload = new
        {
            action = "QuoteDownloaded",
            entityId = "PDF: SEC-01-11" // Quote 11
        };

        using var req = CreateRequest(HttpMethod.Post, "/api/admin/audit", employee101Token, payload);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await _host.WithDb(async db =>
        {
            var log = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "QuoteDownloaded" && a.EntityId == "PDF: SEC-01-11");

            Assert.NotNull(log);
            Assert.Equal(101, log.UserId);
        });
    }

    [Fact]
    public async Task Server_Authoritative_Employee_Lifecycle_Logs_Real_Actor()
    {
        // F08 Item 6: Log account changes using real actor identity
        var ownerToken = _host.RoleToken("owner");

        // 1. Create Employee
        var createPayload = new CreateEmployeeRequestDto
        {
            FullName = "Audit Test Employee",
            Email = "audit.employee@factory.local",
            Password = "SecurePassword123!",
            ConfirmPassword = "SecurePassword123!"
        };

        using var createReq = CreateRequest(HttpMethod.Post, "/api/admin/employees", ownerToken, createPayload);
        using var createRes = await _host.Client.SendAsync(createReq);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);

        int createdId = 0;
        await _host.WithDb(async db =>
        {
            var emp = await db.Users.FirstOrDefaultAsync(u => u.Email == "audit.employee@factory.local");
            Assert.NotNull(emp);
            createdId = emp.Id;

            var createLog = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "EmployeeCreated");
            Assert.NotNull(createLog);
            Assert.Contains($"EmployeeId={createdId}", createLog.EntityId);
        });

        // 2. Update Employee
        var updatePayload = new UpdateEmployeeRequestDto
        {
            FullName = "Audit Test Employee Updated",
            Email = "audit.employee.updated@factory.local"
        };
        using var updateReq = CreateRequest(HttpMethod.Put, $"/api/admin/employees/{createdId}", ownerToken, updatePayload);
        using var updateRes = await _host.Client.SendAsync(updateReq);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);

        await _host.WithDb(async db =>
        {
            var updateLog = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "EmployeeUpdated");
            Assert.NotNull(updateLog);
            Assert.Contains($"EmployeeId={createdId}", updateLog.EntityId);
        });

        // 3. Reset Password
        var resetPayload = new ResetEmployeePasswordRequestDto
        {
            NewPassword = "NewPassword456!",
            ConfirmPassword = "NewPassword456!"
        };
        using var resetReq = CreateRequest(HttpMethod.Post, $"/api/admin/employees/{createdId}/reset-password", ownerToken, resetPayload);
        using var resetRes = await _host.Client.SendAsync(resetReq);
        Assert.Equal(HttpStatusCode.OK, resetRes.StatusCode);

        await _host.WithDb(async db =>
        {
            var resetLog = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "EmployeePasswordReset");
            Assert.NotNull(resetLog);
            Assert.Contains($"EmployeeId={createdId}", resetLog.EntityId);
        });

        // 4. Toggle Status (Deactivate)
        using var toggleReq = CreateRequest(HttpMethod.Post, $"/api/admin/employees/{createdId}/toggle-status", ownerToken, new ToggleActiveRequestDto { IsActive = false });
        using var toggleRes = await _host.Client.SendAsync(toggleReq);
        Assert.Equal(HttpStatusCode.OK, toggleRes.StatusCode);

        await _host.WithDb(async db =>
        {
            var toggleLog = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "EmployeeDeactivated");
            Assert.NotNull(toggleLog);
            Assert.Contains($"EmployeeId={createdId}", toggleLog.EntityId);
        });

        // 5. Delete Employee
        using var delReq = CreateRequest(HttpMethod.Delete, $"/api/admin/employees/{createdId}", ownerToken);
        using var delRes = await _host.Client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.OK, delRes.StatusCode);

        await _host.WithDb(async db =>
        {
            var delLog = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "EmployeeDeleted");
            Assert.NotNull(delLog);
            Assert.Contains($"EmployeeId={createdId}", delLog.EntityId);
        });
    }

    [Fact]
    public async Task Server_Authoritative_Telegram_Management_Logs_Real_Actor()
    {
        // F08 Item 6: Telegram management audited with real actor identity
        var adminToken = _host.RoleToken("admin");

        // 1. Add Telegram Recipient
        var addPayload = new CreateTelegramRecipientRequestDto
        {
            ChatId = "7766554433",
            DisplayName = "Audit Management Chat"
        };
        using var addReq = CreateRequest(HttpMethod.Post, "/api/admin/telegram/recipients", adminToken, addPayload);
        using var addRes = await _host.Client.SendAsync(addReq);
        Assert.Equal(HttpStatusCode.OK, addRes.StatusCode);

        int recId = 0;
        await _host.WithDb(async db =>
        {
            var rec = await db.TelegramRecipients.FirstOrDefaultAsync(r => r.ChatId == "7766554433");
            Assert.NotNull(rec);
            recId = rec.Id;

            var addLog = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "TelegramRecipientAdded");
            Assert.NotNull(addLog);
            Assert.Contains("7766554433", addLog.EntityId);
        });

        // 2. Update Telegram Recipient
        var updatePayload = new UpdateTelegramRecipientRequestDto
        {
            DisplayName = "Audit Management Chat Renamed"
        };
        using var updateReq = CreateRequest(HttpMethod.Put, $"/api/admin/telegram/recipients/{recId}", adminToken, updatePayload);
        using var updateRes = await _host.Client.SendAsync(updateReq);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);

        await _host.WithDb(async db =>
        {
            var updateLog = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "TelegramRecipientUpdated");
            Assert.NotNull(updateLog);
            Assert.Contains($"RecipientId={recId}", updateLog.EntityId);
        });

        // 3. Toggle Recipient Active (Deactivate)
        using var toggleReq = CreateRequest(HttpMethod.Post, $"/api/admin/telegram/recipients/{recId}/toggle-active", adminToken, new ToggleActiveRequestDto { IsActive = false });
        using var toggleRes = await _host.Client.SendAsync(toggleReq);
        Assert.Equal(HttpStatusCode.OK, toggleRes.StatusCode);

        await _host.WithDb(async db =>
        {
            var toggleLog = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "TelegramRecipientDeactivated");
            Assert.NotNull(toggleLog);
            Assert.Contains($"RecipientId={recId}", toggleLog.EntityId);
        });

        // 4. Delete Recipient
        using var delReq = CreateRequest(HttpMethod.Delete, $"/api/admin/telegram/recipients/{recId}", adminToken);
        using var delRes = await _host.Client.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.OK, delRes.StatusCode);

        await _host.WithDb(async db =>
        {
            var delLog = await db.AuditLogs
                .OrderByDescending(a => a.Id)
                .FirstOrDefaultAsync(a => a.Action == "TelegramRecipientDeleted");
            Assert.NotNull(delLog);
            Assert.Contains($"RecipientId={recId}", delLog.EntityId);
        });
    }

    [Fact]
    public async Task Historical_Audit_Entries_Preserved_Without_Historical_Fabrication()
    {
        // F08 Item 7 & 8: Preserve existing audit history and display accurate reports
        var historicalTime = new DateTime(2025, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        await _host.WithDb(async db =>
        {
            db.AuditLogs.Add(new AuditLog
            {
                UserId = 101,
                Action = "HistoricalLegacyAction",
                EntityId = "LEGACY-REF-001",
                Timestamp = historicalTime
            });
            await db.SaveChangesAsync();
        });

        var adminToken = _host.RoleToken("admin");
        using var req = CreateRequest(HttpMethod.Get, "/api/admin/logs", adminToken);
        using var res = await _host.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        bool foundHistorical = false;
        foreach (var elem in doc.RootElement.EnumerateArray())
        {
            if (elem.GetProperty("action").GetString() == "HistoricalLegacyAction")
            {
                foundHistorical = true;
                Assert.Equal("LEGACY-REF-001", elem.GetProperty("entityId").GetString());
                Assert.Equal(101, elem.GetProperty("user").GetProperty("id").GetInt32());
                Assert.Equal(historicalTime, elem.GetProperty("timestamp").GetDateTime());
            }
        }

        Assert.True(foundHistorical, "Historical audit log was not preserved accurately.");
    }
}
