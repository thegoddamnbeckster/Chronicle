using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Chronicle.Data;
using Chronicle.Services.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.Services;

/// <summary>
/// Builds this item's NFO the exact same way the sidecar endpoints already do (an internal,
/// JWT-authenticated loopback call to Chronicle's own API -- see the "why loopback" note below),
/// writes it straight to the item's own on-disk location, then pushes a targeted
/// VideoLibrary.Refresh* to every Kodi instance that already knows this item's internal id.
///
/// Why a loopback HTTP call instead of calling the sidecar-building logic directly: that logic
/// (ScraperController's BuildMovieDetailsDtoAsync/BuildShowDetailsDtoAsync/
/// BuildEpisodeDetailsDtoAsync plus the DTO-to-ResolvedXData mapping) is ~800 lines of
/// already-shipped, already-verified-live controller code with its own web of internal helpers.
/// Re-deriving or relocating all of that to share it with this service would be a large,
/// separately-risky refactor of working code; a single internal HTTP round-trip on
/// localhost is cheap and keeps this feature's own new, unverified code isolated from it.
/// Authenticated with a freshly minted JWT for the item's owning user (Chronicle already mints
/// these the same way for every browser login) rather than any new internal-only bypass.
/// </summary>
public sealed class NfoPushService(
    ChronicleDbContext db,
    IJwtTokenService jwt,
    IHttpClientFactory httpClientFactory,
    IKodiDeviceService devices,
    IKodiRpcClient rpc,
    ILogger<NfoPushService> logger) : INfoPushService
{
    private static readonly string[] MovieLikeTypeNames = ["movies", "fanedits", "anime_movies"];
    private static readonly string[] ShowLikeTypeNames  = ["tv", "anime"];

    public async Task PushAsync(int mediaItemId, int userId, CancellationToken ct = default)
    {
        try
        {
            await PushCoreAsync(mediaItemId, userId, ct);
        }
        catch (OperationCanceledException)
        {
            // Routine (the caller's own ct was cancelled -- e.g. app shutdown mid-push), not a
            // failure -- logged at Info with no stack trace, unlike the genuine-failure case
            // below, so a client disconnect doesn't read as a bug when triaging logs later.
            logger.LogInformation(
                "NfoPushService: push for media item {Id} was cancelled -- the next " +
                "scheduled/manual NFO rebuild will still pick this up.", mediaItemId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "NfoPushService: push failed for media item {Id} -- the next scheduled/manual " +
                "NFO rebuild will still pick this up.", mediaItemId);
        }
    }

    private async Task PushCoreAsync(int mediaItemId, int userId, CancellationToken ct)
    {
        var item = await db.MediaItems.FindAsync([mediaItemId], ct);
        if (item is null) return;

        var mediaType = await db.MediaTypes.FindAsync([item.MediaTypeId], ct);
        if (mediaType is null) return;

        string kind, sidecarPath;
        if (MovieLikeTypeNames.Contains(mediaType.Name))
        {
            // No HierarchyLevel gate here, unlike the show cases below: a standalone movie
            // sits at level 0, but a movie that belongs to a collection sits at level 1 (the
            // collection container itself is level 0) -- confirmed directly against F9, a real
            // member of "The Fast and the Furious Collection" (level 1), which this check
            // originally excluded and silently no-opped for. A collection CONTAINER itself
            // (also movie-typed, also potentially level 0) is harmless to fall through to here:
            // it has no fileScanner location of its own, so the lookup below naturally finds
            // nothing to push.
            kind = "movie";
            sidecarPath = $"/api/v1/scraper/movies/sidecar?id={mediaItemId}";
        }
        else if (ShowLikeTypeNames.Contains(mediaType.Name) && item.HierarchyLevel == 0)
        {
            kind = "tvshow";
            sidecarPath = $"/api/v1/scraper/tv/sidecar?id={mediaItemId}";
        }
        else if (ShowLikeTypeNames.Contains(mediaType.Name) && item.HierarchyLevel == 2)
        {
            kind = "episode";
            sidecarPath = $"/api/v1/scraper/tv/episode-sidecar?id={mediaItemId}";
        }
        else
        {
            return; // not a pushable kind: person, music, season container, collection, etc.
        }

        var (folderPath, filePaths, nfoPath) = ReadFileScannerLocation(item.MetadataJson);
        var destPath = nfoPath ?? DeriveNfoPath(kind, folderPath, filePaths);
        if (destPath is null)
        {
            logger.LogInformation(
                "NfoPushService: item {Id} has no known on-disk location yet -- nothing to push.", mediaItemId);
            return;
        }

        var user = await db.Users.FindAsync([userId], ct);
        if (user is null) return;
        var token = jwt.GenerateToken(user);

        var client = httpClientFactory.CreateClient("internal-loopback");
        using var request = new HttpRequestMessage(HttpMethod.Get, sidecarPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "NfoPushService: couldn't reach Chronicle's own sidecar endpoint for item {Id}.", mediaItemId);
            return;
        }
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "NfoPushService: sidecar build for item {Id} returned HTTP {Status}.",
                mediaItemId, (int)response.StatusCode);
            return;
        }
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        try
        {
            // Write-then-rename, not a direct write to the final path: destPath may be an
            // already-imported item's existing, good NFO. A direct write left mid-flight by a
            // cancelled request or a killed process would leave that file truncated on disk --
            // the same failure class as the earlier Chronicle_Scraper art-corruption saga.
            // File.Move's rename is effectively atomic on both Windows and Linux for a same-
            // volume destination (true here: the temp file is a sibling in the same folder).
            var tempPath = destPath + ".chronicle-tmp";
            await File.WriteAllBytesAsync(tempPath, bytes, ct);
            File.Move(tempPath, destPath, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NfoPushService: couldn't write NFO for item {Id} to {Path}.", mediaItemId, destPath);
            return;
        }
        logger.LogInformation("NfoPushService: wrote NFO for item {Id} to {Path}.", mediaItemId, destPath);

        var targets = await devices.GetPushTargetsAsync(mediaItemId, ct);
        // Fanned out concurrently, not one device at a time: KodiRpcClient.RefreshAsync already
        // caps each call at its own 8s timeout, but a sequential loop would pay that timeout
        // once PER unreachable device instead of once total.
        await Task.WhenAll(targets.Select(async t =>
        {
            var (device, mapping) = t;
            var ok = await rpc.RefreshAsync(device, mapping.Kind, mapping.KodiId, ct);
            logger.LogInformation(
                "NfoPushService: refresh push to device {Device} ({Host}) for item {Id} -- {Result}.",
                device.Name, device.Host, mediaItemId, ok ? "accepted" : "failed");
        }));
    }

    private static (string? FolderPath, List<string>? FilePaths, string? NfoPath) ReadFileScannerLocation(
        string? metadataJson)
    {
        // filePaths goes through FileIdentityJson.ExtractFilePaths -- the codebase's own
        // "single canonical reader for physical-file identity" (see that method's doc) -- rather
        // than a second hand-rolled copy of the same fileScanner.filePaths shape. folderPath/
        // nfoPath have no equivalent shared helper today, so those two stay bespoke here.
        var filePaths = Chronicle.Services.Scan.FileIdentityJson.ExtractFilePaths(metadataJson).ToList();

        if (string.IsNullOrEmpty(metadataJson)) return (null, filePaths.Count > 0 ? filePaths : null, null);
        try
        {
            if (JsonNode.Parse(metadataJson)?.AsObject()["fileScanner"] is not JsonObject fs)
                return (null, filePaths.Count > 0 ? filePaths : null, null);

            var folderPath = fs["folderPath"]?.GetValue<string>();
            var nfoPath     = fs["nfoPath"]?.GetValue<string>();
            return (folderPath, filePaths.Count > 0 ? filePaths : null, nfoPath);
        }
        catch (Exception)
        {
            // Never let a malformed/unexpected fileScanner shape (JsonException from Parse, or
            // InvalidOperationException from AsObject()/GetValue<string>() hitting the wrong
            // JSON kind) escape this best-effort lookup -- PushAsync's own outer catch would
            // still save the request, but "no location yet" is a much clearer log line than a
            // stack trace for what's ultimately just missing/odd metadata.
            return (null, filePaths.Count > 0 ? filePaths : null, null);
        }
    }

    /// <summary>Same naming convention Chronicle_Scraper's nfo_writer.py/tv_nfo_writer.py use:
    /// "tvshow.nfo" for a show's own root, the real video file's own basename otherwise.
    /// Only reached when this item has never had an NFO before (nfoPath unset) -- an
    /// already-scraped item's own recorded nfoPath is always preferred over re-deriving this.</summary>
    private static string? DeriveNfoPath(string kind, string? folderPath, List<string>? filePaths)
    {
        if (string.IsNullOrEmpty(folderPath)) return null;
        if (kind == "tvshow") return Path.Combine(folderPath, "tvshow.nfo");

        var videoFile = filePaths?.FirstOrDefault();
        if (string.IsNullOrEmpty(videoFile)) return null;
        var stem = Path.GetFileNameWithoutExtension(videoFile);
        return Path.Combine(folderPath, stem + ".nfo");
    }
}
