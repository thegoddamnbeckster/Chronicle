using Chronicle.Data;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Security;

public class MediaRightsPolicyTests : IDisposable
{
    private readonly ChronicleDbContext _db = new(new DbContextOptionsBuilder<ChronicleDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task DeletionIsOpenByDefault() =>
        (await new MediaRightsPolicy(_db).IsDeleteAdminOnlyAsync()).Should().BeFalse();

    [Fact]
    public async Task CanBeReservedForAdministrators_AndOpenedAgain_WithOneStoredRow()
    {
        var policy = new MediaRightsPolicy(_db);
        await policy.SetDeleteAdminOnlyAsync(true);
        (await policy.IsDeleteAdminOnlyAsync()).Should().BeTrue();

        await policy.SetDeleteAdminOnlyAsync(false);
        (await policy.IsDeleteAdminOnlyAsync()).Should().BeFalse();
        (await _db.AppSettings.CountAsync(s => s.Key == MediaRightsPolicy.DeleteAdminOnlyKey)).Should().Be(1);
    }

    [Fact]
    public async Task AGarbledStoredValue_MeansOpen()
    {
        _db.AppSettings.Add(new Chronicle.Core.Models.AppSetting { Key = MediaRightsPolicy.DeleteAdminOnlyKey, Value = "maybe" });
        await _db.SaveChangesAsync();
        (await new MediaRightsPolicy(_db).IsDeleteAdminOnlyAsync()).Should().BeFalse();
    }
}
