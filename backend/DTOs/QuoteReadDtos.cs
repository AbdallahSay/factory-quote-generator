namespace FactoryQuoteApi.DTOs;

// Read contract deliberately contains business data only, never EF navigation entities.
public class QuoteReadResponseDto
{
    public int Id { get; set; }
    public string QuoteNumber { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string? ClientEmail { get; set; }
    public string? ClientPhone { get; set; }
    public string ContactPerson { get; set; } = string.Empty;
    public string? ContactTitle { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string? IssuerName { get; set; }
    public string? IssuerJobTitle { get; set; }
    public string? IssuerPrefix { get; set; }
    public string? TermsJson { get; set; }
    public decimal TotalAmount { get; set; }
    public string? PdfUrl { get; set; }
    public string? DocxUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<QuoteReadItemDto> Items { get; set; } = new();
}

public class QuoteReadItemDto
{
    public int Id { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Size { get; set; } = string.Empty;
    public string Capacity { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
}
