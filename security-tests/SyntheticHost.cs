using System.Net;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FactoryQuoteApi.Controllers;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.DTOs;
using FactoryQuoteApi.Models;
using FactoryQuoteApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FactoryQuoteApi.SecurityTests;

// Real HTTP routing, MVC serialization, SQL projections and session validation.
// Production Program/startup/configuration and all real notification transports are excluded.
internal sealed class SyntheticHost
{
    internal const string SigningKey = "synthetic-security-test-key-000000000000000000000000";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "factory-sec01-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Dictionary<int, User> _users = new();
    internal WebApplication App { get; private set; } = null!;
    internal HttpClient Client { get; private set; } = null!;
    internal SyntheticTelegramTransport Transport { get; } = new();

    internal async Task StartAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Templates"));
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot"));
        using (var doc = WordprocessingDocument.Create(Path.Combine(_root, "Templates", "عرض سعر شركة اتريم.docx"), DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            doc.AddMainDocumentPart().Document = new Document(new Body(new Paragraph(new Run(new Text("{{QuoteNumber}}")))));
        await _connection.OpenAsync();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _root, WebRootPath = "wwwroot", EnvironmentName = "SecurityTests" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Security:SigningKey"] = SigningKey,
            ["Telegram:BotToken"] = "123456:synthetic-not-a-real-token",
            ["Telegram:ChatIds"] = "synthetic-chat"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(QuotesController).Assembly)
            .AddJsonOptions(o => o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);
        builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(_connection));
        builder.Services.AddScoped<ISecurityService, SecurityService>();
        builder.Services.AddScoped<IWordQuoteGeneratorService, WordQuoteGeneratorService>();
        builder.Services.AddSingleton<IPdfConverterService, SyntheticPdfConverter>();
        builder.Services.AddSingleton<IQuoteSequenceService, SyntheticSequence>();
        builder.Services.AddSingleton<IBackgroundTaskQueue, BackgroundTaskQueue>();
        builder.Services.AddScoped<ITelegramDeliveryService, TelegramDeliveryService>();
        builder.Services.AddSingleton<INotificationService, NoNotifications>();
        builder.Services.AddSingleton<IHttpClientFactory>(new SyntheticHttpClientFactory(Transport));
        App = builder.Build();
        App.MapControllers();
        await WithDb(async db =>
        {
            await db.Database.EnsureCreatedAsync(); // Local in-memory SQLite only.
            foreach (var user in new[] {
                new User { Id = 1, FullName = "Legacy shared identity", Email = "legacy@example.invalid", Role = "Admin", PasswordHash = "synthetic-password-material-admin" },
                new User { Id = 101, FullName = "Synthetic Employee A", Email = "a@example.invalid", Role = "Sales", PasswordHash = "synthetic-password-material-a" },
                new User { Id = 102, FullName = "Synthetic Employee B", Email = "b@example.invalid", Role = "Sales", PasswordHash = "synthetic-password-material-b" }
            }) { _users.Add(user.Id, user); db.Users.Add(user); }
            foreach (var role in new[] { "admin", "owner", "quote", "unexpected" })
                db.SystemCredentials.Add(new SystemCredential { Role = role, SessionSalt = "synthetic-session-salt-" + role, PasswordHash = "synthetic-password-material" });
            foreach (var (id, userId, creatorId) in new (int, int, int?)[] { (11, 101, 101), (12, 102, 102), (13, 101, null), (14, 101, 102), (15, 1, 1) })
                db.Quotes.Add(new Quote { Id = id, QuoteNumber = "SEC-01-" + id, UserId = userId, CreatedByUserId = creatorId,
                    ClientName = "Synthetic client " + id, ProjectName = "Synthetic project", TotalAmount = 6,
                    PdfUrl = "https://synthetic.invalid/quotes/" + id + ".pdf", DocxUrl = "https://synthetic.invalid/quotes/" + id + ".docx",
                    Items = new List<QuoteItem> { new() { ProductName = "Synthetic item", Quantity = 2, UnitPrice = 3, LineTotal = 6 } } });
            await db.SaveChangesAsync();
        });
        await App.StartAsync();
        Client = App.GetTestClient();
    }

    internal string EmployeeToken(int id, TimeSpan? lifetime = null)
    {
        using var scope = App.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISecurityService>().GenerateUserToken(_users[id], lifetime ?? TimeSpan.FromHours(1));
    }

    internal string RoleToken(string role)
    {
        using var scope = App.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISecurityService>().GenerateToken(role, "synthetic-session-salt-" + role, TimeSpan.FromHours(1));
    }

    internal async Task WithDb(Func<ApplicationDbContext, Task> action)
    {
        using var scope = App.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    internal async Task DisposeAsync()
    {
        Client?.Dispose();
        if (App != null) await App.DisposeAsync();
        await _connection.DisposeAsync();
        // Only the test-created, unique temporary fixture directory is eligible for removal.
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(_root)), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(_root).StartsWith("factory-sec01-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to remove a directory outside the test fixture location.");
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

internal sealed class SyntheticTelegramTransport : HttpMessageHandler
{
    internal int Sends { get; private set; }
    internal string LastBody { get; private set; } = string.Empty;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "api.telegram.org" || !request.RequestUri.AbsolutePath.EndsWith("/sendDocument"))
            throw new InvalidOperationException("Unexpected outbound request in synthetic test.");
        Sends++;
        LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":123,\"document\":{\"file_id\":\"synthetic-file\"}}}") };
    }
}

internal sealed class SyntheticHttpClientFactory(SyntheticTelegramTransport transport) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(transport, disposeHandler: false);
}

internal sealed class SyntheticPdfConverter : IPdfConverterService
{
    public async Task<string> ConvertDocxToPdfAsync(string docxPath, string pdfPath, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(pdfPath, "%PDF- synthetic fixture", cancellationToken);
        return pdfPath;
    }
}

internal sealed class SyntheticSequence : IQuoteSequenceService
{
    private int _value = 1000;
    public Task EnsureInitializedAsync() => Task.CompletedTask;
    public Task<string> GetNextQuoteNumberAsync(DateTime date) => Task.FromResult($"{Interlocked.Increment(ref _value):D6}-{date:dd-MM-yyyy}");
    public Task<string> PeekNextQuoteNumberAsync(DateTime date) => Task.FromResult($"{_value + 1:D6}-{date:dd-MM-yyyy}");
}

internal sealed class NoNotifications : INotificationService
{
    public Task SendEmailNotificationAsync(string a, string b, string c, decimal d, string e, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No email should be requested in these tests.");
    public Task SendWhatsAppWebhookAsync(WebhookPayloadDto payload, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No WhatsApp call should be requested in these tests.");
}
