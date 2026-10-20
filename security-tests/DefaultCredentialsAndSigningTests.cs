using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using FactoryQuoteApi.Data;
using FactoryQuoteApi.DTOs;
using FactoryQuoteApi.Models;
using FactoryQuoteApi.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FactoryQuoteApi.SecurityTests;

public sealed class DefaultCredentialsAndSigningTests : IAsyncLifetime
{
    private readonly SyntheticHost _host = new();
    public Task InitializeAsync() => _host.StartAsync();
    public Task DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task EnsureInitializedAsync_Never_Seeds_Default_Or_Hardcoded_Passwords()
    {
        // Setup isolated in-memory DB without prior credentials
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        // Configure empty configuration with a valid signing key
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:SigningKey"] = "Valid_Secure_HighEntropy_SigningKey_32Chars_Long!"
        }).Build();

        var service = new SecurityService(db, config, NullLogger<SecurityService>.Instance);
        await service.EnsureInitializedAsync();

        // 1. Verify legacy 'quote' row is NOT created
        var quoteCred = await db.SystemCredentials.FirstOrDefaultAsync(s => s.Role == "quote");
        Assert.Null(quoteCred);

        // 2. Verify no default passwords like Quote@2026, Admin@2026, Owner@Master2026 exist
        var allCreds = await db.SystemCredentials.ToListAsync();
        Assert.Empty(allCreds);

        // 3. Verify no users are seeded with placeholder hashes
        var allUsers = await db.Users.ToListAsync();
        Assert.Empty(allUsers);
    }

    [Theory]
    [InlineData("<YOUR_HMAC_SIGNING_SECRET>")]
    [InlineData("FactoryQuote_Fallback_Secret_Key_ChangeInProduction_2026")]
    [InlineData("short_key")]
    [InlineData("1234567890123456789012345678901")] // 31 chars
    [InlineData("CHANGEME_SECRET_KEY_PLACEHOLDER_KEY_12345")]
    [InlineData(null)]
    [InlineData("")]
    public void SigningKey_Validation_Rejects_Weak_Or_Placeholder_Keys(string? weakKey)
    {
        bool isValid = SecurityService.ValidateSigningKeyStrength(weakKey, out string? reason);
        Assert.False(isValid);
        Assert.NotNull(reason);
    }

    [Fact]
    public void SigningKey_Validation_Accepts_Strong_HighEntropy_Key()
    {
        string strongKey = "FactoryQuote_Prod_HMAC_SecretKey_2026_!#SafeMasterKey99";
        bool isValid = SecurityService.ValidateSigningKeyStrength(strongKey, out string? reason);
        Assert.True(isValid);
        Assert.Null(reason);
    }

    [Fact]
    public async Task KeyRotation_DualKey_Validates_Previous_Key_Tokens()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        string oldKey = "Old_Active_Signing_Key_For_Rotation_Test_32Chars!";
        string newKey = "New_Primary_Signing_Key_For_Rotation_Test_32Chars!";

        // 1. Generate token using old service
        var oldConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:SigningKey"] = oldKey
        }).Build();
        var oldService = new SecurityService(db, oldConfig, NullLogger<SecurityService>.Instance);

        var employee = new User
        {
            Id = 42,
            FullName = "Rotating Employee",
            Email = "rot@example.invalid",
            Role = "Sales",
            PasswordHash = "hash1234567890123456",
            IsActive = true
        };
        db.Users.Add(employee);
        await db.SaveChangesAsync();

        string tokenFromOldKey = oldService.GenerateUserToken(employee, TimeSpan.FromHours(1));

        // 2. Configure new service with dual keys (new primary + old in PreviousSigningKeys)
        var newConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:SigningKey"] = newKey,
            ["Security:PreviousSigningKeys"] = oldKey
        }).Build();
        var rotatedService = new SecurityService(db, newConfig, NullLogger<SecurityService>.Instance);

        var (isValid, user, role) = await rotatedService.ValidateUserTokenAsync(tokenFromOldKey);
        Assert.True(isValid);
        Assert.NotNull(user);
        Assert.Equal(42, user.Id);
        Assert.Equal("employee", role);

        // 3. Now test that if old key is removed, the token is rejected
        var singleNewConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:SigningKey"] = newKey
        }).Build();
        var strictNewService = new SecurityService(db, singleNewConfig, NullLogger<SecurityService>.Instance);

        var (rejectedValid, _, _) = await strictNewService.ValidateUserTokenAsync(tokenFromOldKey);
        Assert.False(rejectedValid);
    }

    [Fact]
    public async Task Owner_Provisioning_OneTimeBootstrap_Workflow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        string bootstrapToken = "Secure_Bootstrap_Token_2026_OneTime!";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:SigningKey"] = "Valid_Secure_HighEntropy_SigningKey_32Chars_Long!",
            ["Security:BootstrapToken"] = bootstrapToken
        }).Build();

        var service = new SecurityService(db, config, NullLogger<SecurityService>.Instance);

        // 1. Wrong bootstrap token should fail
        var (fail1, msg1) = await service.ProvisionInitialOwnerAsync("WrongToken", "StrongOwnerPass@2026");
        Assert.False(fail1);
        Assert.Equal("رمز التهيئة غير صحيح.", msg1);

        // 2. Correct bootstrap token should succeed
        var (success, _) = await service.ProvisionInitialOwnerAsync(bootstrapToken, "StrongOwnerPass@2026");
        Assert.True(success);

        var ownerCred = await db.SystemCredentials.FirstOrDefaultAsync(s => s.Role == "owner");
        Assert.NotNull(ownerCred);
        Assert.True(service.VerifyPassword("StrongOwnerPass@2026", ownerCred.PasswordHash));

        // 3. Second bootstrap attempt must fail (one-time only)
        var (fail2, msg2) = await service.ProvisionInitialOwnerAsync(bootstrapToken, "AnotherPassword@2026");
        Assert.False(fail2);
        Assert.Contains("تم تعيين مالك النظام مسبقاً", msg2);
    }

    [Fact]
    public async Task Owner_Recovery_Resets_OwnerPassword_And_Invalidates_Prior_Sessions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureCreatedAsync();

        string recoveryKey = "Secure_Recovery_Key_For_Disaster_2026!";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:SigningKey"] = "Valid_Secure_HighEntropy_SigningKey_32Chars_Long!",
            ["Security:RecoveryKey"] = recoveryKey
        }).Build();

        var service = new SecurityService(db, config, NullLogger<SecurityService>.Instance);

        // Seed initial owner
        var initialSalt = Guid.NewGuid().ToString("N");
        db.SystemCredentials.Add(new SystemCredential
        {
            Role = "owner",
            PasswordHash = service.HashPassword("OldOwnerPassword@123"),
            SessionSalt = initialSalt,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // Generate owner token with initial salt
        string oldOwnerToken = service.GenerateToken("owner", initialSalt, TimeSpan.FromHours(1));

        // Token should be valid before recovery
        var (validBefore, _) = await service.ValidateTokenAsync(oldOwnerToken, "owner");
        Assert.True(validBefore);

        // Recover with correct key
        var (recoverSuccess, _) = await service.RecoverOwnerCredentialAsync(recoveryKey, "BrandNewOwnerPassword@2026");
        Assert.True(recoverSuccess);

        // Old token must now be invalid due to session salt regeneration
        var (validAfter, _) = await service.ValidateTokenAsync(oldOwnerToken, "owner");
        Assert.False(validAfter);

        // Login with new password must succeed
        var (loginSuccess, newToken, _) = await service.AuthenticateAsync("owner", "BrandNewOwnerPassword@2026");
        Assert.True(loginSuccess);
        Assert.NotNull(newToken);
    }
}
