using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Chronicle.Core.Models;
using Chronicle.Services.Notifications;

namespace Chronicle.API.Authentication;

/// <summary>
/// Tells the administrators when one of the Kodi add-ons is being turned away for its API key. The add-ons warn on the TV for fifteen
/// seconds, which is easy to miss from the sofa, and the server used to record the rejection only in the log. The notification names the
/// add-on and the address it came from, and stays one unread notification per add-on and address until it is read.
/// </summary>
public static partial class KodiRejectionAlert
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, DateTime> LastChecked = new();

    [GeneratedRegex(@"^Kodi/Chronicle-(?<name>[A-Za-z0-9]+)/", RegexOptions.CultureInvariant)]
    private static partial Regex AgentPattern();

    /// <summary>"Chronicle Scrobbler" for the agent "Kodi/Chronicle-Scrobbler/1.0"; null for anything else (a browser, a script).</summary>
    public static string? AddOnName(string? userAgent)
    {
        var m = userAgent is null ? null : AgentPattern().Match(userAgent);
        return m is { Success: true } ? "Chronicle " + m.Groups["name"].Value : null;
    }

    public static async Task RaiseAsync(HttpContext ctx, string reason, CancellationToken ct = default)
    {
        var addOn = AddOnName(ctx.Request.Headers.UserAgent.ToString());
        if (addOn is null) return;
        var ip = ctx.Connection.RemoteIpAddress is { IsIPv4MappedToIPv6: true } v4 ? v4.MapToIPv4().ToString() : ctx.Connection.RemoteIpAddress?.ToString() ?? "an unknown address";

        var key = $"{addOn}|{ip}";
        var now = DateTime.UtcNow;
        if (LastChecked.TryGetValue(key, out var last) && now - last < Quiet) return;
        LastChecked[key] = now;

        try
        {
            var notifications = ctx.RequestServices.GetService<INotificationService>();
            if (notifications is null) return;
            await notifications.NotifyAdminsAsync(NotificationKinds.DeviceRejected,
                $"{addOn} on {ip} was turned away",
                $"Chronicle rejected the API key that {addOn} on {ip} sent ({reason}), so nothing from it is syncing. " +
                "On that Kodi, open the add-on's settings and choose Edit Connection to reconnect it.",
                "/settings/api-keys", dedupeKey: $"device.rejected:{key}", ct: ct);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Could not raise the rejected-key notification for {AddOn}", addOn);   // never fail a request over an alert
        }
    }
}
