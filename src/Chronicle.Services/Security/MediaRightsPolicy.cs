using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services.Security;

/// <summary>
/// Who may delete or merge away the library's shared media. The library is shared: one person's delete removes the item for
/// everyone. Open to every signed-in user by default (the long-standing behaviour); an administrator can reserve it for administrators.
/// </summary>
public interface IMediaRightsPolicy
{
    /// <summary>True when only administrators may delete or merge media.</summary>
    Task<bool> IsDeleteAdminOnlyAsync(CancellationToken ct = default);

    Task SetDeleteAdminOnlyAsync(bool adminOnly, CancellationToken ct = default);
}

public sealed class MediaRightsPolicy(ChronicleDbContext db) : IMediaRightsPolicy
{
    public const string DeleteAdminOnlyKey = "auth.media_delete_admin_only";

    public async Task<bool> IsDeleteAdminOnlyAsync(CancellationToken ct = default)
    {
        // Straight from the table, never the one-minute settings cache: a change must take effect at once.
        var value = await db.AppSettings.AsNoTracking().Where(s => s.Key == DeleteAdminOnlyKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetDeleteAdminOnlyAsync(bool adminOnly, CancellationToken ct = default)
    {
        var text = adminOnly ? "true" : "false";
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == DeleteAdminOnlyKey, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = DeleteAdminOnlyKey, Value = text });
        else row.Value = text;
        await db.SaveChangesAsync(ct);
    }
}
