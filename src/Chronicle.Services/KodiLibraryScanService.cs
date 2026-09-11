using System.Collections.Concurrent;
using Chronicle.Core.Helpers;
using Chronicle.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

public sealed class KodiLibraryScanService(
    IServiceScopeFactory scopeFactory,
    ILogger<KodiLibraryScanService> logger) : IKodiLibraryScanService
{
    // Deliberately in-memory only, not persisted -- this only needs to survive the current
    // process's lifetime. A restart resetting it just means the next call after startup
    // triggers one scan per device it otherwise would have skipped; nothing worth a migration
    // (and a schema column) over. Registered as a singleton (see Program.cs) specifically so
    // this dictionary is shared across every call, not reset every time a caller resolves this
    // service from a fresh DI scope.
    //
    // Keyed by device id alone, not (device, mediaTypeId) -- deliberately not per-type. A scan
    // folder run can call NotifyNewContentAsync once per folder (a movies folder and a TV
    // folder both creating new items in the same run means two calls, one per type), but
    // ScanAsync's own VideoLibrary.Scan has no "just this type" scope -- it's Kodi's whole
    // video library, movies and TV together. So a device already asked to scan moments ago for
    // one type has, as a side effect, also just been asked about the other; skipping it again
    // here is correct, not a missed notification.
    private static readonly TimeSpan MinIntervalBetweenScans = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<int, DateTime> _lastTriggeredUtc = new();

    public async Task NotifyNewContentAsync(int mediaTypeId, CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db      = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var devices = scope.ServiceProvider.GetRequiredService<IKodiDeviceService>();
        var rpc     = scope.ServiceProvider.GetRequiredService<IKodiRpcClient>();

        var mediaType = await db.MediaTypes.FindAsync([mediaTypeId], ct);
        if (mediaType is null || !NfoKindHelper.IsVideoLibraryType(mediaType.Name)) return;

        var allDevices = await devices.GetAllActiveDevicesAsync(ct);
        if (allDevices.Count == 0) return;

        var now = DateTime.UtcNow;
        var due = allDevices.Where(d => TryClaim(d.Id, now)).ToList();
        if (due.Count == 0)
        {
            logger.LogDebug(
                "KodiLibraryScanService: new {MediaType} content found but every registered device was scanned within the last {Minutes}m -- skipping this trigger.",
                mediaType.Name, MinIntervalBetweenScans.TotalMinutes);
            return;
        }

        // Fanned out concurrently, same rationale as NfoPushService's own device loop:
        // ScanAsync already caps each call at its own timeout, but a sequential loop would pay
        // that timeout once PER unreachable device instead of once total.
        await Task.WhenAll(due.Select(async device =>
        {
            var ok = await rpc.ScanAsync(device, ct);
            logger.LogInformation(
                "KodiLibraryScanService: library scan trigger to {Device} ({Host}) -- {Result}.",
                device.Name, device.Host, ok ? "accepted" : "failed");
        }));
    }

    /// <summary>Atomically checks-and-claims one device's throttle slot. Returns true (and
    /// records `nowUtc`) only when enough time has passed since this device's last claim; a
    /// device still within its window is left untouched and this returns false. Concurrent
    /// callers claiming the same device only ever result in one of them winning per window --
    /// AddOrUpdate's factories run under the dictionary's own per-key synchronization, so there
    /// is no race between the "is it due" check and the "record it" write the way there would
    /// be with a separate TryGetValue + write.</summary>
    private bool TryClaim(int deviceId, DateTime nowUtc)
    {
        var claimed = false;
        _lastTriggeredUtc.AddOrUpdate(
            deviceId,
            addValueFactory: _ => { claimed = true; return nowUtc; },
            updateValueFactory: (_, last) =>
            {
                if (nowUtc - last < MinIntervalBetweenScans) return last;
                claimed = true;
                return nowUtc;
            });
        return claimed;
    }
}
