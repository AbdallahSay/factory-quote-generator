using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using FactoryQuoteApi.DTOs;

namespace FactoryQuoteApi.Services;

public interface INotificationService
{
    Task SendEmailNotificationAsync(string toEmail, string clientName, string quoteNumber, decimal totalAmount, string pdfFilePath, CancellationToken cancellationToken = default);
    Task SendWhatsAppWebhookAsync(WebhookPayloadDto payload, CancellationToken cancellationToken = default);
}

public class NotificationService : INotificationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<NotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendEmailNotificationAsync(
        string toEmail,
        string clientName,
        string quoteNumber,
        decimal totalAmount,
        string pdfFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toEmail))
        {
            _logger.LogWarning("Email address is empty. Skipping email notification for quote {QuoteNumber}.", quoteNumber);
            return;
        }

        var smtpHost = _configuration["Smtp:Host"];
        var smtpPort = _configuration.GetValue<int>("Smtp:Port", 587);
        var smtpUser = _configuration["Smtp:Username"];
        var smtpPass = _configuration["Smtp:Password"];
        var fromEmail = _configuration["Smtp:FromEmail"] ?? "sales@factory-eg.com";
        var fromName = _configuration["Smtp:FromName"] ?? "مصنع الطوب الآلي والخرسانة";

        if (string.IsNullOrWhiteSpace(smtpHost))
        {
            _logger.LogInformation("[Email Mock] SMTP host not configured. Mocking email delivery to {Email} for Quote {QuoteNumber}.", toEmail, quoteNumber);
            return;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress(clientName, toEmail));
            message.Subject = $"عرض أسعار رقم {quoteNumber} - {fromName}";

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = $@"
<div dir=""rtl"" style=""font-family: Arial, Tahoma, sans-serif; line-height: 1.8; color: #333;"">
    <h2 style=""color: #1e3a8a;"">السادة / {clientName} المحترمين</h2>
    <p>تحية طيبة وبعد،،،</p>
    <p>يسرنا أن نرفق لكم عرض الأسعار الخاص بطلبكم رقم: <strong>{quoteNumber}</strong> بإجمالي قدره: <strong>{totalAmount:N2} جنيه مصري</strong>.</p>
    <p>تجدون في المرفقات التفاصيل الكاملة للمنتجات والمواصفات وشروط التوريد والدفع.</p>
    <br/>
    <p>شاكرين لكم حسن تعاونكم الدائم،،،</p>
    <p><strong>إدارة المبيعات</strong><br/>{fromName}</p>
</div>"
            };

            if (File.Exists(pdfFilePath))
            {
                await bodyBuilder.Attachments.AddAsync(pdfFilePath, cancellationToken);
            }

            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(smtpHost, smtpPort, SecureSocketOptions.Auto, cancellationToken);
            if (!string.IsNullOrEmpty(smtpUser))
            {
                await client.AuthenticateAsync(smtpUser, smtpPass, cancellationToken);
            }
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            _logger.LogInformation("Email notification sent successfully to {Email} for quote {QuoteNumber}.", toEmail, quoteNumber);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email notification to {Email} for quote {QuoteNumber}.", toEmail, quoteNumber);
        }
    }

    public async Task SendWhatsAppWebhookAsync(WebhookPayloadDto payload, CancellationToken cancellationToken = default)
    {
        var webhookUrl = _configuration["Webhooks:WhatsAppUrl"];
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            _logger.LogWarning("WhatsApp Webhook URL not configured. Skipping webhook dispatch for quote {QuoteNumber}.", payload.QuoteNumber);
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true
            });

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            _logger.LogInformation("Sending WhatsApp Webhook to {Url} for quote {QuoteNumber}...", webhookUrl, payload.QuoteNumber);

            var response = await client.PostAsync(webhookUrl, content, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("WhatsApp Webhook dispatched successfully to {Url} (Status: {StatusCode}).", webhookUrl, response.StatusCode);
            }
            else
            {
                _logger.LogWarning("WhatsApp Webhook returned non-success status code: {StatusCode} for URL {Url}.", response.StatusCode, webhookUrl);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch WhatsApp Webhook to {Url} for quote {QuoteNumber}.", webhookUrl, payload.QuoteNumber);
        }
    }
}
