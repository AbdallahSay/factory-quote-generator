namespace FactoryQuoteApi.Services;

public interface ISecurityService
{
    Task EnsureInitializedAsync();
    string HashPassword(string password);
    bool VerifyPassword(string password, string storedHash);
    string GenerateToken(string role, string sessionSalt, TimeSpan expiry);
    Task<(bool IsValid, string? Role)> ValidateTokenAsync(string? token, params string[] allowedRoles);
    Task<(bool Success, string? Token, string? ErrorMessage)> AuthenticateAsync(string role, string password);
    Task<(bool Success, string? ErrorMessage)> ChangePasswordAsync(string targetRole, string newPassword);
}
