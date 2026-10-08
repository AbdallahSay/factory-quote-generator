namespace FactoryQuoteApi.Models;

public class SystemCredential
{
    public string Role { get; set; } = string.Empty; // "quote", "admin", "owner"
    public string PasswordHash { get; set; } = string.Empty;
    public string SessionSalt { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
