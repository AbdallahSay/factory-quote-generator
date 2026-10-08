using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.Models;

namespace FactoryQuoteApi.Services;

public class SecurityService : ISecurityService
{
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SecurityService> _logger;

    private readonly byte[] _signingKey;
    private const int Pbkdf2Iterations = 100000;
    private const int SaltByteSize = 16;
    private const int HashByteSize = 32;

    public SecurityService(ApplicationDbContext db, IConfiguration configuration, ILogger<SecurityService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;

        var keyString = _configuration["Security:SigningKey"] 
                        ?? _configuration["Security:JwtSecret"] 
                        ?? "FactoryQuote_Prod_HMAC_SecretKey_2026_!#SafeMasterKey99";
        _signingKey = Encoding.UTF8.GetBytes(keyString);
    }

    public async Task EnsureInitializedAsync()
    {
        try
        {
            // 1. Ensure table exists in SQL Server
            await _db.Database.ExecuteSqlRawAsync(@"
                IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'SystemCredentials')
                BEGIN
                    CREATE TABLE SystemCredentials (
                        Role NVARCHAR(50) NOT NULL PRIMARY KEY,
                        PasswordHash NVARCHAR(255) NOT NULL,
                        SessionSalt NVARCHAR(100) NOT NULL,
                        UpdatedAt DATETIME2 NOT NULL
                    );
                END
            ");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not run raw SQL check for SystemCredentials table (may already exist or provider does not support sys.tables).");
        }

        try
        {
            // 2. Seed default credentials if missing
            var defaultQuotePassword = _configuration["Security:DefaultQuotePassword"];
            if (string.IsNullOrWhiteSpace(defaultQuotePassword) || defaultQuotePassword.StartsWith("<"))
                defaultQuotePassword = "Quote@2026";

            var defaultAdminPassword = _configuration["Security:DefaultAdminPassword"];
            if (string.IsNullOrWhiteSpace(defaultAdminPassword) || defaultAdminPassword.StartsWith("<"))
                defaultAdminPassword = "Admin@2026";

            var defaultOwnerPassword = _configuration["Security:DefaultOwnerPassword"];
            if (string.IsNullOrWhiteSpace(defaultOwnerPassword) || defaultOwnerPassword.StartsWith("<"))
                defaultOwnerPassword = "Owner@Master2026";

            var rolesToSeed = new Dictionary<string, string>
            {
                { "quote", defaultQuotePassword },
                { "admin", defaultAdminPassword },
                { "owner", defaultOwnerPassword }
            };

            bool changed = false;
            foreach (var kvp in rolesToSeed)
            {
                var role = kvp.Key;
                var rawPassword = kvp.Value;
                var exists = await _db.SystemCredentials.AnyAsync(s => s.Role == role);
                if (!exists)
                {
                    _logger.LogInformation("Seeding default credential for role: {Role}", role);
                    _db.SystemCredentials.Add(new SystemCredential
                    {
                        Role = role,
                        PasswordHash = HashPassword(rawPassword),
                        SessionSalt = Guid.NewGuid().ToString("N"),
                        UpdatedAt = DateTime.UtcNow
                    });
                    changed = true;
                }
            }

            if (changed)
            {
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed default system credentials.");
        }
    }

    public string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltByteSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            HashByteSize
        );

        return $"pbkdf2:{Pbkdf2Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public bool VerifyPassword(string password, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash) || string.IsNullOrWhiteSpace(password))
            return false;

        var parts = storedHash.Split(':');
        if (parts.Length != 4 || parts[0] != "pbkdf2")
            return false;

        if (!int.TryParse(parts[1], out int iterations))
            return false;

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expectedHash = Convert.FromBase64String(parts[3]);
        }
        catch
        {
            return false;
        }

        byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length
        );

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    public string GenerateToken(string role, string sessionSalt, TimeSpan expiry)
    {
        long expiryUnix = DateTimeOffset.UtcNow.Add(expiry).ToUnixTimeSeconds();
        string payload = $"{role}|{sessionSalt}|{expiryUnix}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        string payloadB64 = Base64UrlEncode(payloadBytes);

        using var hmac = new HMACSHA256(_signingKey);
        byte[] sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadB64));
        string sigB64 = Base64UrlEncode(sigBytes);

        return $"{payloadB64}.{sigB64}";
    }

    public async Task<(bool IsValid, string? Role)> ValidateTokenAsync(string? token, params string[] allowedRoles)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, null);

        var parts = token.Trim().Split('.');
        if (parts.Length != 2)
            return (false, null);

        string payloadB64 = parts[0];
        string sigB64 = parts[1];

        // 1. Verify HMAC
        using var hmac = new HMACSHA256(_signingKey);
        byte[] expectedSig = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadB64));
        byte[] actualSig;
        try
        {
            actualSig = Base64UrlDecode(sigB64);
        }
        catch
        {
            return (false, null);
        }

        if (!CryptographicOperations.FixedTimeEquals(actualSig, expectedSig))
            return (false, null);

        // 2. Decode payload
        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Base64UrlDecode(payloadB64));
        }
        catch
        {
            return (false, null);
        }

        var segments = payload.Split('|');
        if (segments.Length != 3)
            return (false, null);

        string role = segments[0];
        string tokenSalt = segments[1];
        if (!long.TryParse(segments[2], out long expiryUnix))
            return (false, null);

        // 3. Check expiration
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiryUnix)
            return (false, null);

        // 4. Role checking
        if (allowedRoles.Length > 0 && !allowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
            return (false, null);

        // 5. Database session salt match check (ensures invalidated tokens upon password change)
        var cred = await _db.SystemCredentials.FirstOrDefaultAsync(s => s.Role == role);
        if (cred == null || cred.SessionSalt != tokenSalt)
            return (false, null);

        return (true, role);
    }

    public async Task<(bool Success, string? Token, string? ErrorMessage)> AuthenticateAsync(string role, string password)
    {
        var normalizedRole = role?.Trim().ToLowerInvariant();
        if (normalizedRole != "quote" && normalizedRole != "admin" && normalizedRole != "owner")
        {
            return (false, null, "نوع الصلاحية غير صحيح");
        }

        var cred = await _db.SystemCredentials.FirstOrDefaultAsync(s => s.Role == normalizedRole);
        if (cred == null)
        {
            return (false, null, "بيانات الاعتماد غير موجودة");
        }

        if (!VerifyPassword(password, cred.PasswordHash))
        {
            return (false, null, "كلمة المرور غير صحيحة");
        }

        // Issue token valid for 7 days
        string token = GenerateToken(normalizedRole, cred.SessionSalt, TimeSpan.FromDays(7));
        return (true, token, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> ChangePasswordAsync(string targetRole, string newPassword)
    {
        var normalizedRole = targetRole?.Trim().ToLowerInvariant();
        if (normalizedRole != "quote" && normalizedRole != "admin")
        {
            return (false, "يمكن تغيير كلمة مرور عروض الأسعار أو لوحة الإدارة فقط");
        }

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6)
        {
            return (false, "يجب أن تكون كلمة المرور 6 أحرف على الأقل");
        }

        var cred = await _db.SystemCredentials.FirstOrDefaultAsync(s => s.Role == normalizedRole);
        if (cred == null)
        {
            cred = new SystemCredential
            {
                Role = normalizedRole
            };
            _db.SystemCredentials.Add(cred);
        }

        cred.PasswordHash = HashPassword(newPassword);
        // Regenerating SessionSalt automatically invalidates all previous sessions for this role!
        cred.SessionSalt = Guid.NewGuid().ToString("N");
        cred.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        _logger.LogInformation("Password successfully updated and sessions invalidated for role: {Role}", normalizedRole);

        return (true, null);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }

    private static byte[] Base64UrlDecode(string input)
    {
        string padded = input.Replace("-", "+").Replace("_", "/");
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
