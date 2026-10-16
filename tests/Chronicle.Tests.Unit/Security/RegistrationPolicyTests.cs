using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Security;

public class RegistrationPolicyTests : IDisposable
{
    private readonly ChronicleDbContext _db = new(new DbContextOptionsBuilder<ChronicleDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private void AddUser() { _db.Users.Add(new User { Username = "a", PasswordHash = "x", IsActive = true }); _db.SaveChanges(); }

    [Fact]
    public async Task DefaultsToOpen()
    {
        AddUser();
        (await new RegistrationPolicy(_db).IsClosedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Locked_ClosesSignUp_ButNeverForTheFirstAccountOnAnEmptyInstall()
    {
        var policy = new RegistrationPolicy(_db);
        await policy.SetLockedAsync(true);
        (await policy.IsClosedAsync()).Should().BeFalse("nobody could ever create the first administrator otherwise");

        AddUser();
        (await policy.IsClosedAsync()).Should().BeTrue();

        await policy.SetLockedAsync(false);
        (await policy.IsClosedAsync()).Should().BeFalse();
        (await _db.AppSettings.CountAsync(s => s.Key == RegistrationPolicy.SettingKey)).Should().Be(1);
    }
}
