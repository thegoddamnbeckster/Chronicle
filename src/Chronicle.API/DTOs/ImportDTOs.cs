namespace Chronicle.API.DTOs;

// ── Provider list ─────────────────────────────────────────────────────────────

/// <summary>
/// Summary of a loaded import provider shown in GET /import/providers.
/// The frontend uses this to render the import management page.
/// </summary>
public record ImportProviderDto(
    string PluginId,
    string Name,
    string Version,
    string Description,
    bool   SupportsHistory,
    bool   SupportsRatings,
    bool   SupportsWatchlist,
    bool   RequiresDeviceAuth
);

// ── Auth flow ─────────────────────────────────────────────────────────────────

/// <summary>
/// Returned when the device/PIN auth flow is started.
/// The client should display <see cref="UserCode"/> and <see cref="VerificationUrl"/>
/// to the user, then poll <c>GET .../auth/poll/{pollCode}</c> every
/// <see cref="PollingIntervalSeconds"/> seconds.
/// </summary>
public record StartAuthResponse(
    string UserCode,
    string VerificationUrl,
    int    ExpiresInSeconds,
    int    PollingIntervalSeconds,
    /// <summary>Pass this to the poll endpoint — may differ from UserCode.</summary>
    string PollCode
);

/// <summary>Result of a single poll attempt.</summary>
public record PollAuthResponse(
    /// <summary>pending | authorized | expired | denied</summary>
    string  Status,
    string? ErrorMessage = null
);

public record AuthStatusResponse(bool Authenticated);

// ── Import results ────────────────────────────────────────────────────────────

public record ImportResultResponse(
    int          Imported,
    int          Skipped,
    List<string> Errors
);
