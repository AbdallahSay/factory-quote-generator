using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FactoryQuoteApi.Models;

public class User
{
    public int Id { get; set; }
    [Required, MaxLength(150)]
    public string FullName { get; set; } = string.Empty;
    [Required, MaxLength(150)]
    public string Email { get; set; } = string.Empty;
    [Required]
    public string PasswordHash { get; set; } = string.Empty;
    [Required, MaxLength(50)]
    public string Role { get; set; } = "Sales"; // Admin or Sales
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Quote> Quotes { get; set; } = new List<Quote>();
    public ICollection<AuditLog> AuditLogs { get; set; } = new List<AuditLog>();
}

public class Client
{
    public int Id { get; set; }
    [Required, MaxLength(200)]
    public string ClientName { get; set; } = string.Empty;
    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;
    [MaxLength(150)]
    public string Email { get; set; } = string.Empty;
    [MaxLength(150)]
    public string ContactPerson { get; set; } = string.Empty;
    [MaxLength(250)]
    public string Address { get; set; } = string.Empty;
    public int? CreatedById { get; set; }
    public User? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Product
{
    public int Id { get; set; }
    [Required, MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;
    [MaxLength(100)]
    public string Size { get; set; } = string.Empty;
    [MaxLength(100)]
    public string Capacity { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")]
    public decimal DefaultPrice { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Quote
{
    public int Id { get; set; }
    [Required, MaxLength(50)]
    public string QuoteNumber { get; set; } = string.Empty;
    public int UserId { get; set; }
    public User? User { get; set; }
    [Required, MaxLength(200)]
    public string ClientName { get; set; } = string.Empty;
    [MaxLength(150)]
    public string? ClientEmail { get; set; }
    [MaxLength(50)]
    public string? ClientPhone { get; set; }
    [MaxLength(150)]
    public string ContactPerson { get; set; } = string.Empty;
    [MaxLength(200)]
    public string ProjectName { get; set; } = string.Empty;
    [MaxLength(200)]
    public string Location { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }
    [MaxLength(50)]
    public string? ContactTitle { get; set; }
    [MaxLength(150)]
    public string? IssuerName { get; set; }
    [MaxLength(150)]
    public string? IssuerJobTitle { get; set; }
    [MaxLength(50)]
    public string? IssuerPrefix { get; set; }
    public string? TermsJson { get; set; }
    [MaxLength(500)]
    public string? PdfUrl { get; set; }
    [MaxLength(500)]
    public string? DocxUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<QuoteItem> Items { get; set; } = new List<QuoteItem>();
}

public class QuoteItem
{
    public int Id { get; set; }
    public int QuoteId { get; set; }
    public Quote? Quote { get; set; }
    [Required, MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;
    [MaxLength(100)]
    public string Size { get; set; } = string.Empty;
    [MaxLength(100)]
    public string Capacity { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")]
    public decimal Quantity { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }
}

public class AuditLog
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    [Required, MaxLength(100)]
    public string Action { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? EntityId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
