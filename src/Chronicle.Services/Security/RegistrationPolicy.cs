using Chronicle.Data;
using Chronicle.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services.Security;

/// <summary>Whether strangers may create their own accounts. Unlocked by default; an administrator can lock it.</summary>
public interface IRegistrationPolicy
{
    /// <summary>True when self-service sign-up is closed. The very first account on an empty install is always allowed.</summary>
    Task<bool> IsClosedAsync(CancellationToken ct = default);

    Task<bool> IsLockedSettingAsync(CancellationToken ct = default);
    Task SetLockedAsync(bool locked, CancellationToken ct = default);
}

public sealed class RegistrationPolicy(ChronicleDbContext db) : IRegistrationPolicy
{
    public const string SettingKey = "auth.registration_locked";

    public async Task<bool> IsLockedSettingAsync(CancellationToken ct = default)
    {
        // Read straight from the table, never the one-minute settings cache: flipping the lock must take effect at once.
        var value = await db.AppSettings.AsNoTracking().Where(s => s.Key == SettingKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> IsClosedAsync(CancellationToken ct = default) =>
        await IsLockedSettingAsync(ct) && await db.Users.AnyAsync(ct);

    public async Task SetLockedAsync(bool locked, CancellationToken ct = default)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingKey, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = SettingKey, Value = locked ? "true" : "false" });
        else row.Value = locked ? "true" : "false";
        await db.SaveChangesAsync(ct);
    }
}
