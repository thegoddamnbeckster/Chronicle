using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Security;

public class ApiTokenServiceTests : IDisposable
{
    private readonly ChronicleDbContext _context;
    private readonly ApiTokenService _service;

    // A user seeded for all tests
    private readonly User _user;

    public ApiTokenServiceTests()
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new ChronicleDbContext(options);

        _user = new User
        {
            Username = "testuser",
            PasswordHash = "hash",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _context.Users.Add(_user);
        _context.SaveChanges();

        _service = new ApiTokenService(_context);
    }

    public void Dispose() => _context.Dispose();

    // ── CreateTokenAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTokenAsync_ReturnsRawTokenWithPrefix()
    {
        var (_, rawValue) = await _service.CreateTokenAsync(_user.Id, "Test", null);

        rawValue.Should().StartWith("chr_live_");
        rawValue.Length.Should().Be(9 + 32); // "chr_live_" + 32 hex chars
    }

    [Fact]
    public async Task CreateTokenAsync_PersistsHashedToken_NotRawValue()
    {
        var (token, rawValue) = await _service.CreateTokenAsync(_user.Id, "Test", null);

        var stored = await _context.ApiTokens.FindAsync(token.Id);
        stored.Should().NotBeNull();
        stored!.Token.Should().NotBe(rawValue);       // hash is stored, not raw
        stored.Token.Length.Should().Be(64);           // SHA-256 hex = 64 chars
    }

    [Fact]
    public async Task CreateTokenAsync_SetsIsActiveTrue()
    {
        var (token, _) = await _service.CreateTokenAsync(_user.Id, "Test", null);

        var stored = await _context.ApiTokens.FindAsync(token.Id);
        stored!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task CreateTokenAsync_WithExpiry_PersistsExpiry()
    {
        var expiry = DateTime.UtcNow.AddDays(30);
        var (token, _) = await _service.CreateTokenAsync(_user.Id, "Expiring", expiry);

        var stored = await _context.ApiTokens.FindAsync(token.Id);
        stored!.ExpiresAt.Should().BeCloseTo(expiry, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateTokenAsync_TwoCallsProduceDifferentRawValues()
    {
        var (_, raw1) = await _service.CreateTokenAsync(_user.Id, "Key1", null);
        var (_, raw2) = await _service.CreateTokenAsync(_user.Id, "Key2", null);

        raw1.Should().NotBe(raw2);
    }

    // ── ValidateTokenAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ValidateTokenAsync_ValidToken_ReturnsTokenWithUser()
    {
        var (_, rawValue) = await _service.CreateTokenAsync(_user.Id, "Valid", null);

        var result = await _service.ValidateTokenAsync(rawValue);

        result.Should().NotBeNull();
        result!.UserId.Should().Be(_user.Id);
        result.User.Should().NotBeNull();
        result.User!.Username.Should().Be("testuser");
    }

    [Fact]
    public async Task ValidateTokenAsync_ValidToken_UpdatesLastUsedAt()
    {
        var (token, rawValue) = await _service.CreateTokenAsync(_user.Id, "Valid", null);
        token.LastUsedAt.Should().BeNull();

        await _service.ValidateTokenAsync(rawValue);

        var updated = await _context.ApiTokens.FindAsync(token.Id);
        updated!.LastUsedAt.Should().NotBeNull();
        updated.LastUsedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ValidateTokenAsync_UnknownToken_ReturnsNull()
    {
        var result = await _service.ValidateTokenAsync("chr_live_" + new string('a', 32));

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_TokenWithoutPrefix_ReturnsNull()
    {
        var result = await _service.ValidateTokenAsync("notavalidtoken");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_EmptyString_ReturnsNull()
    {
        var result = await _service.ValidateTokenAsync(string.Empty);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_RevokedToken_ReturnsNull()
    {
        var (token, rawValue) = await _service.CreateTokenAsync(_user.Id, "Revoked", null);
        await _service.RevokeTokenAsync(token.Id, _user.Id);

        var result = await _service.ValidateTokenAsync(rawValue);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateTokenAsync_ExpiredToken_ReturnsNull()
    {
        var (token, rawValue) = await _service.CreateTokenAsync(
            _user.Id, "Expired", DateTime.UtcNow.AddSeconds(-1));

        var result = await _service.ValidateTokenAsync(rawValue);

        result.Should().BeNull();
    }

    // ── GetTokensForUserAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetTokensForUserAsync_ReturnsOnlyActiveTokensForUser()
    {
        var (t1, _) = await _service.CreateTokenAsync(_user.Id, "Key1", null);
        var (t2, _) = await _service.CreateTokenAsync(_user.Id, "Key2", null);
        await _service.RevokeTokenAsync(t2.Id, _user.Id);

        var list = await _service.GetTokensForUserAsync(_user.Id);

        list.Should().ContainSingle(t => t.Id == t1.Id);
        list.Should().NotContain(t => t.Id == t2.Id);
    }

    [Fact]
    public async Task GetTokensForUserAsync_DoesNotReturnOtherUsersTokens()
    {
        var other = new User
        {
            Username = "other",
            PasswordHash = "hash",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _context.Users.Add(other);
        await _context.SaveChangesAsync();

        await _service.CreateTokenAsync(other.Id, "OtherKey", null);
        await _service.CreateTokenAsync(_user.Id, "MyKey", null);

        var list = await _service.GetTokensForUserAsync(_user.Id);

        list.Should().ContainSingle();
        list[0].UserId.Should().Be(_user.Id);
    }

    // ── RevokeTokenAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeTokenAsync_OwnedToken_ReturnsTrueAndDeactivates()
    {
        var (token, _) = await _service.CreateTokenAsync(_user.Id, "ToRevoke", null);

        var result = await _service.RevokeTokenAsync(token.Id, _user.Id);

        result.Should().BeTrue();
        var stored = await _context.ApiTokens.FindAsync(token.Id);
        stored!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeTokenAsync_WrongUser_ReturnsFalse()
    {
        var other = new User
        {
            Username = "other2",
            PasswordHash = "hash",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _context.Users.Add(other);
        await _context.SaveChangesAsync();

        var (token, _) = await _service.CreateTokenAsync(_user.Id, "Mine", null);

        var result = await _service.RevokeTokenAsync(token.Id, other.Id);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RevokeTokenAsync_AlreadyRevoked_ReturnsFalse()
    {
        var (token, _) = await _service.CreateTokenAsync(_user.Id, "AlreadyGone", null);
        await _service.RevokeTokenAsync(token.Id, _user.Id);

        var result = await _service.RevokeTokenAsync(token.Id, _user.Id);

        result.Should().BeFalse();
    }
}
