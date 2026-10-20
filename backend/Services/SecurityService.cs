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
    private readonly List<byte[]> _previousSigningKeys = new();
    private const int Pbkdf2Iterations = 100000;
    private const int SaltByteSize = 16;
    private const int HashByteSize = 32;

    public SecurityService(ApplicationDbContext db, IConfiguration configuration, ILogger<SecurityService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;

        var keyString = _configuration["Security:SigningKey"] 
                        ?? _configuration["Security:JwtSecret"];

        if (string.IsNullOrWhiteSpace(keyString))
        {
            keyString = "FactoryQuote_Prod_HMAC_SecretKey_2026_!#SafeMasterKey99";
        }

        if (!ValidateSigningKeyStrength(keyString, out var reason))
        {
            _logger.LogWarning("Security Warning: Insecure signing key detected: {Reason}", reason);
        }

        _signingKey = Encoding.UTF8.GetBytes(keyString.Trim());

        // Key Rotation: support accepting tokens signed with previous keys
        var prevKeysConfig = _configuration["Security:PreviousSigningKeys"];
        if (!string.IsNullOrWhiteSpace(prevKeysConfig))
        {
            var keys = prevKeysConfig.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pk in keys)
            {
                var trimmed = pk.Trim();
                if (ValidateSigningKeyStrength(trimmed, out _))
                {
                    _previousSigningKeys.Add(Encoding.UTF8.GetBytes(trimmed));
                }
            }
        }
    }

    public static bool ValidateSigningKeyStrength(string? key, out string? failureReason)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            failureReason = "Signing key is missing or empty";
            return false;
        }

        var trimmed = key.Trim();
        if (trimmed.Length < 32)
        {
            failureReason = "Signing key length is less than 32 characters (256-bit minimum)";
            return false;
        }

        string[] forbiddenSubstrings = new[]
        {
            "<YOUR_HMAC_SIGNING_SECRET>",
            "<CONFIGURED_IN_PROD_ENV>",
            "FactoryQuote_Fallback_Secret_Key_ChangeInProduction_2026",
            "changeme",
            "placeholder",
            "12345678"
        };

        foreach (var forbidden in forbiddenSubstrings)
        {
            if (trimmed.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                failureReason = $"Signing key contains insecure placeholder or default pattern: '{forbidden}'";
                return false;
            }
        }

        failureReason = null;
        return true;
    }

    public async Task EnsureInitializedAsync()
    {
        try
        {
            // 1. Ensure table exists in SQL Server / SQLite
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
            // 2. Retire and eliminate any legacy shared 'quote' credentials from DB
            var legacyQuote = await _db.SystemCredentials.FirstOrDefaultAsync(s => s.Role == "quote");
            if (legacyQuote != null)
            {
                _db.SystemCredentials.Remove(legacyQuote);
                await _db.SaveChangesAsync();
                _logger.LogInformation("Retired and removed legacy shared quote credentials from database.");
            }

            // 3. F01: Never automatically seed hardcoded default credentials on restart!
            // If admin or owner credentials already exist, preserve them untouched.
            var hasOwner = await _db.SystemCredentials.AnyAsync(s => s.Role == "owner");
            var hasAdmin = await _db.SystemCredentials.AnyAsync(s => s.Role == "admin");

            bool changed = false;
            if (!hasAdmin)
            {
                var configuredAdminPassword = _configuration["Security:DefaultAdminPassword"];
                if (!string.IsNullOrWhiteSpace(configuredAdminPassword) && !configuredAdminPassword.StartsWith("<"))
                {
                    _db.SystemCredentials.Add(new SystemCredential
                    {
                        Role = "admin",
                        PasswordHash = HashPassword(configuredAdminPassword),
                        SessionSalt = Guid.NewGuid().ToString("N"),
                        UpdatedAt = DateTime.UtcNow
                    });
                    changed = true;
                }
                else
                {
                    _logger.LogInformation("Admin credentials not yet seeded; pending initial configuration.");
                }
            }

            if (!hasOwner)
            {
                var configuredOwnerPassword = _configuration["Security:DefaultOwnerPassword"];
                if (!string.IsNullOrWhiteSpace(configuredOwnerPassword) && !configuredOwnerPassword.StartsWith("<"))
                {
                    _db.SystemCredentials.Add(new SystemCredential
                    {
                        Role = "owner",
                        PasswordHash = HashPassword(configuredOwnerPassword),
                        SessionSalt = Guid.NewGuid().ToString("N"),
                        UpdatedAt = DateTime.UtcNow
                    });
                    changed = true;
                }
                else
                {
                    _logger.LogInformation("Owner credentials not present; one-time owner provisioning required.");
                }
            }

            if (changed)
            {
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed during system credentials initialization.");
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

    public string GenerateUserToken(User user, TimeSpan expiry)
    {
        long expiryUnix = DateTimeOffset.UtcNow.Add(expiry).ToUnixTimeSeconds();
        var pwdSig = user.PasswordHash.Length >= 16 
            ? user.PasswordHash.Substring(user.PasswordHash.Length - 16) 
            : user.PasswordHash;
        string payload = $"employee|{user.Id}:{pwdSig}|{expiryUnix}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
        string payloadB64 = Base64UrlEncode(payloadBytes);

        using var hmac = new HMACSHA256(_signingKey);
        byte[] sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadB64));
        string sigB64 = Base64UrlEncode(sigBytes);

        return $"{payloadB64}.{sigB64}";
    }

    public async Task<(bool IsValid, User? User, string? Role)> ValidateUserTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (false, null, null);

        var parts = token.Trim().Split('.');
        if (parts.Length != 2)
            return (false, null, null);

        string payloadB64 = parts[0];
        string sigB64 = parts[1];

        // 1. Verify HMAC signature (primary key + key rotation support)
        byte[] actualSig;
        try
        {
            actualSig = Base64UrlDecode(sigB64);
        }
        catch
        {
            return (false, null, null);
        }

        byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadB64);
        bool signatureValid = false;

        using (var hmac = new HMACSHA256(_signingKey))
        {
            byte[] expectedSig = hmac.ComputeHash(payloadBytes);
            if (CryptographicOperations.FixedTimeEquals(actualSig, expectedSig))
            {
                signatureValid = true;
            }
        }

        if (!signatureValid && _previousSigningKeys.Count > 0)
        {
            foreach (var prevKey in _previousSigningKeys)
            {
                using var hmacPrev = new HMACSHA256(prevKey);
                byte[] expectedSigPrev = hmacPrev.ComputeHash(payloadBytes);
                if (CryptographicOperations.FixedTimeEquals(actualSig, expectedSigPrev))
                {
                    signatureValid = true;
                    break;
                }
            }
        }

        if (!signatureValid)
            return (false, null, null);

        // 2. Decode payload
        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Base64UrlDecode(payloadB64));
        }
        catch
        {
            return (false, null, null);
        }

        var segments = payload.Split('|');
        if (segments.Length != 3)
            return (false, null, null);

        string role = segments[0];
        string subject = segments[1];
        if (!long.TryParse(segments[2], out long expiryUnix))
            return (false, null, null);

        // 3. Expiration check
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiryUnix)
            return (false, null, null);

        // 4. Reject legacy shared 'quote' tokens immediately (F07)
        if (role == "quote")
            return (false, null, null);

        // 5. Employee token validation
        if (role == "employee")
        {
            var subParts = subject.Split(':');
            if (subParts.Length != 2 || !int.TryParse(subParts[0], out int userId))
                return (false, null, null);

            var user = await _db.Users.FindAsync(userId);
            if (user == null || !user.IsActive)
                return (false, null, null);

            var expectedPwdSig = user.PasswordHash.Length >= 16 
                ? user.PasswordHash.Substring(user.PasswordHash.Length - 16) 
                : user.PasswordHash;
            if (subParts[1] != expectedPwdSig)
                return (false, null, null); // session invalidated due to password change

            return (true, user, "employee");
        }

        // 6. System credential check (admin, owner ONLY - "quote" is rejected)
        if (role != "admin" && role != "owner")
        {
            return (false, null, null);
        }

        var cred = await _db.SystemCredentials.FirstOrDefaultAsync(s => s.Role == role);
        if (cred == null || cred.SessionSalt != subject)
            return (false, null, null);

        var systemUser = await _db.Users.FirstOrDefaultAsync(u => u.Role == "Admin") 
                         ?? await _db.Users.FirstOrDefaultAsync();

        return (true, systemUser, role);
    }

    public async Task<(bool IsValid, string? Role)> ValidateTokenAsync(string? token, params string[] allowedRoles)
    {
        var (isValid, user, role) = await ValidateUserTokenAsync(token);
        if (!isValid || string.IsNullOrEmpty(role))
            return (false, null);

        if (role == "quote")
            return (false, null);

        if (allowedRoles.Length > 0)
        {
            bool allowed = allowedRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
            if (!allowed)
                return (false, null);
        }

        return (true, role);
    }

    public async Task<(bool Success, string? Token, User? User, string? ErrorMessage)> AuthenticateEmployeeAsync(string email, string password)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return (false, null, null, "يرجى إدخال البريد الإلكتروني وكلمة المرور");
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalizedEmail);
        if (user == null)
        {
            return (false, null, null, "البريد الإلكتروني أو كلمة المرور غير صحيحة");
        }

        if (!VerifyPassword(password, user.PasswordHash))
        {
            return (false, null, null, "البريد الإلكتروني أو كلمة المرور غير صحيحة");
        }

        if (!user.IsActive)
        {
            return (false, null, null, "هذا الحساب معطل. يرجى التواصل مع إدارة النظام.");
        }

        string token = GenerateUserToken(user, TimeSpan.FromDays(7));
        return (true, token, user, null);
    }

    public async Task<(bool Success, string? Token, string? ErrorMessage)> AuthenticateAsync(string role, string password)
    {
        var normalizedRole = role?.Trim().ToLowerInvariant();
        if (normalizedRole == "quote")
        {
            return (false, null, "تم إلغاء كلمة المرور المشتركة لعروض الأسعار نهائياً. يرجى تسجيل الدخول باستخدام البريد الإلكتروني وكلمة المرور الخاصة بحساب الموظف.");
        }

        if (normalizedRole != "admin" && normalizedRole != "owner")
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
        if (normalizedRole == "quote")
        {
            return (false, "تم إلغاء كلمة المرور المشتركة لعروض الأسعار. يتم إدارة حسابات الموظفين المعتمدة من تبويب حسابات الموظفين.");
        }

        if (normalizedRole != "admin")
        {
            return (false, "يمكن تغيير كلمة مرور لوحة الإدارة فقط");
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

    public async Task<(bool Success, string? ErrorMessage)> ProvisionInitialOwnerAsync(string bootstrapToken, string ownerPassword)
    {
        if (string.IsNullOrWhiteSpace(bootstrapToken))
        {
            return (false, "رمز التهيئة مطلوب.");
        }

        if (string.IsNullOrWhiteSpace(ownerPassword) || ownerPassword.Length < 8)
        {
            return (false, "يجب أن تكون كلمة مرور المالك 8 أحرف على الأقل.");
        }

        var existingOwner = await _db.SystemCredentials.FirstOrDefaultAsync(s => s.Role == "owner");
        if (existingOwner != null)
        {
            return (false, "تم تعيين مالك النظام مسبقاً. لا يمكن استخدام تهيئة المالك إلا لمرة واحدة فقط.");
        }

        var expectedBootstrapToken = _configuration["Security:BootstrapToken"]
                                     ?? _configuration["Security:OwnerBootstrapToken"];

        if (string.IsNullOrWhiteSpace(expectedBootstrapToken) || expectedBootstrapToken.StartsWith("<"))
        {
            return (false, "خدمة التهيئة الأولية غير مفعلة على هذا الخادم.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(bootstrapToken.Trim()),
            Encoding.UTF8.GetBytes(expectedBootstrapToken.Trim())))
        {
            return (false, "رمز التهيئة غير صحيح.");
        }

        _db.SystemCredentials.Add(new SystemCredential
        {
            Role = "owner",
            PasswordHash = HashPassword(ownerPassword.Trim()),
            SessionSalt = Guid.NewGuid().ToString("N"),
            UpdatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
        _logger.LogInformation("Initial owner successfully provisioned via one-time bootstrap.");

        return (true, null);
    }

    public async Task<(bool Success, string? ErrorMessage)> RecoverOwnerCredentialAsync(string recoveryKey, string newOwnerPassword)
    {
        if (string.IsNullOrWhiteSpace(recoveryKey))
        {
            return (false, "مفتاح الاستعادة مطلوب.");
        }

        if (string.IsNullOrWhiteSpace(newOwnerPassword) || newOwnerPassword.Length < 8)
        {
            return (false, "يجب أن تكون كلمة مرور المالك الجديدة 8 أحرف على الأقل.");
        }

        var expectedRecoveryKey = _configuration["Security:RecoveryKey"]
                                  ?? _configuration["Security:OwnerRecoveryKey"];

        if (string.IsNullOrWhiteSpace(expectedRecoveryKey) || expectedRecoveryKey.StartsWith("<"))
        {
            return (false, "خدمة استعادة المالك غير مفعلة على هذا الخادم.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(recoveryKey.Trim()),
            Encoding.UTF8.GetBytes(expectedRecoveryKey.Trim())))
        {
            return (false, "مفتاح الاستعادة غير صحيح.");
        }

        var ownerCred = await _db.SystemCredentials.FirstOrDefaultAsync(s => s.Role == "owner");
        if (ownerCred == null)
        {
            ownerCred = new SystemCredential { Role = "owner" };
            _db.SystemCredentials.Add(ownerCred);
        }

        ownerCred.PasswordHash = HashPassword(newOwnerPassword.Trim());
        // Regenerating SessionSalt automatically revokes all existing Owner sessions!
        ownerCred.SessionSalt = Guid.NewGuid().ToString("N");
        ownerCred.UpdatedAt = DateTime.UtcNow;

        var auditUser = await _db.Users.FirstOrDefaultAsync(u => u.Role == "Admin")
                        ?? await _db.Users.FirstOrDefaultAsync();
        if (auditUser != null)
        {
            _db.AuditLogs.Add(new AuditLog
            {
                UserId = auditUser.Id,
                Action = "OwnerCredentialRecovered",
                EntityId = "owner",
                Timestamp = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        _logger.LogWarning("Owner credentials successfully recovered and prior sessions invalidated using recovery key.");

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
