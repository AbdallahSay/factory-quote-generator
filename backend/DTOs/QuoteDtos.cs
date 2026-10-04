using System.ComponentModel.DataAnnotations;

namespace FactoryQuoteApi.DTOs;

public class ProductItemDto
{
    [Required]
    public string ProductName { get; set; } = string.Empty;
    public string? Size { get; set; }
    public string? Capacity { get; set; }
    [Range(0.01, double.MaxValue, ErrorMessage = "Quantity must be greater than 0")]
    public decimal Quantity { get; set; }
    [Range(0.0, double.MaxValue, ErrorMessage = "UnitPrice must be non-negative")]
    public decimal UnitPrice { get; set; }
}

public class QuoteRequestDto
{
    public int UserId { get; set; } = 1; // Default to seeded Sales user
    [Required(ErrorMessage = "ClientName is required")]
    public string ClientName { get; set; } = string.Empty;
    public string? ContactPerson { get; set; }
    public string? ClientEmail { get; set; }
    public string? ClientPhone { get; set; }
    public string? ProjectName { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public string? PaymentTerms { get; set; }
    public int? ValidityDays { get; set; }
    public decimal? TotalAmount { get; set; }

    // Dynamic Table Structure (Mini-Excel Grid)
    public List<string> Headers { get; set; } = new();
    public List<Dictionary<string, string>> Rows { get; set; } = new();

    // Legacy items list for backwards compatibility
    public List<ProductItemDto> Items { get; set; } = new();
}

public class QuoteResponseDto
{
    public int QuoteId { get; set; }
    public string QuoteNumber { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string PdfUrl { get; set; } = string.Empty;
    public string DocxUrl { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class WebhookPayloadDto
{
    public string Event { get; set; } = "QuoteGenerated";
    public string QuoteNumber { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string ClientPhone { get; set; } = string.Empty;
    public string? ClientEmail { get; set; }
    public decimal TotalAmount { get; set; }
    public string PdfUrl { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
