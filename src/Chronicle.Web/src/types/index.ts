// ── Auth ──────────────────────────────────────────────────────────────────────
export interface User {
  id: number
  username: string
  email: string | null
  displayName: string | null
  isAdmin: boolean
  showDiagnostics: boolean
  showNowPlayingBanner: boolean
  /** Person pages show every credit (including titles not in the library) by default. */
  showAllCredits: boolean
}

/** A signed-in browser session. The key itself is never sent back to the client. */
export interface SessionInfo {
  id: string
  createdAt: string
  lastSeenAt: string
  expiresAt: string
  userAgent: string | null
  remoteIp: string | null
  isCurrent: boolean
}

export interface AuthResponse {
  token: string
  user: User
}

// ── Media ─────────────────────────────────────────────────────────────────────
export interface ExternalId {
  source: string
  externalId: string
}

export interface FileScannerMeta {
  filePath: string | null
  localPosterPath: string | null
  importedAt: string | null
}

export interface RefreshLog {
  providerName: string
  refreshedAt: string
  succeeded: boolean
  errorMessage?: string | null
}

export interface MediaItem {
  id: number
  mediaTypeId: number
  mediaTypeName: string
  parentId: number | null
  name: string
  year: number | null
  overview: string | null
  posterUrl: string | null
  runtimeMinutes: number | null
  hierarchyLevel: number
  ancestors?: { id: number; name: string }[]
  number: number | null
  /** Precise (possibly fractional) position within a book series, e.g. 1.1 for a companion
   *  novella between books 1 and 2. Null for every item other than a book placed in a series
   *  with a known Hardcover position -- fall back to `number` when absent. */
  seriesPosition?: number | null
  createdAt: string
  updatedAt: string
  externalIds: ExternalId[]
  fileScannerMeta?: FileScannerMeta | null
  /** All plugin metadata keyed by full plugin ID (e.g. "chronicle.plugin.tmdb").
   *  Values are raw JSON objects from each plugin — no typed shapes enforced here. */
  pluginMetadata?: Record<string, Record<string, unknown>> | null
  refreshLogs?: RefreshLog[] | null
  /** Enrichment attempt status per plugin, keyed by plugin ID.
   *  Present even when pluginMetadata has no entry (e.g. status is "NotFound").
   *  Values: "Pending" | "Completed" | "NotFound" | "Failed" | "Exhausted" */
  enrichmentStatuses?: Record<string, string> | null
  /** Canonical internal media type name (e.g. "tv", "movies", "music").
   *  Used for plugin compatibility checks. mediaTypeName is the user-facing display
   *  name (e.g. "TV Shows") and should be used for display only. */
  mediaTypeInternalName?: string | null
  /** True when this item or any descendant has a tracked physical file on disk. */
  hasPhysicalFile?: boolean | null
  /** True when this item or any leaf descendant lacks a tracked physical file.
   *  Set for both the pure metadata-only case (no files anywhere in the subtree)
   *  and the mixed case (some leaves have files, some do not). */
  hasMetadataOnly?: boolean | null
  /** Alternative names this item is known by (recorded during merges). */
  aliases?: string[] | null
  /** History of merges where this item is the winner. */
  mergeHistory?: MergeHistoryEntry[] | null
  /** Merged metadata resolved by walking each field's plugin priority list.
   *  The first non-empty value from the highest-priority plugin wins per field. */
  resolvedMetadata?: {
    title?: string | null
    overview?: string | null
    year?: number | null
    posterUrl?: string | null
    backdropUrl?: string | null
    runtimeMinutes?: number | null
    rating?: number | null
    genres?: string[] | null
    cast?: { name: string; role?: string | null }[] | null
    crew?: { name: string; job?: string | null }[] | null
    tags?: string[] | null
    logoUrl?: string | null
    bannerUrl?: string | null
    clearartUrl?: string | null
    discUrl?: string | null
    characterArtUrl?: string | null
    thumbUrl?: string | null
  } | null
  /** True when this is a Level 0 movies item that acts as a collection container. */
  isCollectionContainer?: boolean
  /** True when this movie was auto-created as a collection stub (not yet owned by the user). */
  isStub?: boolean
  /** Manually-pinned image/field overrides, keyed by canonical field name (e.g. "poster_url").
   *  A pinned field always wins over the normal plugin-priority resolution walk until cleared. */
  overrides?: Record<string, {
    url: string
    sourcePluginId?: string | null
    sourceType?: string | null
    pinnedAt: string
    pinnedByUserId?: number | null
  }> | null
  /** Promoted canonical fields -- populated for "people" items (Wikipedia) and solo music
   *  artists (MusicBrainz's own life-span data); null for every other type/item. */
  birthDate?: string | null
  deathDate?: string | null
}

export interface MergeHistoryEntry {
  mergeId: number
  loserOriginalId: number
  loserName: string
  mergedAt: string
  mergedByUserId: number | null
}

// ── Library ───────────────────────────────────────────────────────────────────
export type LibraryStatus =
  | 'Unwatched'
  | 'PlanToWatch'
  | 'Watching'
  | 'Completed'
  | 'Dropped'
  | 'OnHold'
  | 'Rewatching'

// ── People ───────────────────────────────────────────────────────────────────
// People are catalog-wide, not per-user library items -- no watch-status concept applies
// (docs/plans/2026-08-28-people-section-design.md Section 1.4). A person's own detail reuses
// the generic MediaItem/MediaItemDto shape (fetched via the normal GET /media/:id endpoint);
// these two types are only for the parts that ARE People-specific: the catalog list and the
// role-grouped credits section on the detail page.
export interface PersonListItem {
  id: number
  name: string
  posterUrl: string | null
  birthDate: string | null
  deathDate: string | null
  roles: string[]
  /** The character this person plays on the title this list is scoped to (MediaDetailPage's
   * cast section) -- always null on the catalog-wide People grid, which has no single title
   * to attribute a character to. */
  characterName?: string | null
}

export interface PersonCredit {
  mediaItemId: number
  name: string
  posterUrl: string | null
  year: number | null
  mediaTypeName: string
  characterName: string | null
}

/** A credit on the person page's "every credit" view; mediaItemId is null for a title not in the library. */
export interface PersonFullCredit {
  mediaItemId: number | null
  name: string
  posterUrl: string | null
  year: number | null
  mediaTypeName: string
  characterName: string | null
}

/** The "every credit" response; incomplete means the provider list could not be fetched. */
export interface PersonAllCredits {
  groups: PersonFullCreditGroup[]
  incomplete: boolean
}

export interface PersonFullCreditGroup {
  role: string
  items: PersonFullCredit[]
}

export interface PersonCreditGroup {
  role: string
  items: PersonCredit[]
}

export interface PersonHeadshot {
  id: number
  url: string
  thumbnailUrl: string | null
  source: string
  firstSeenAt: string
  isCurrent: boolean
}

export interface LibraryEntry {
  id: number
  userId: number
  mediaItem: MediaItem
  status: LibraryStatus
  userRating: number | null
  userRatingSource: string | null
  notes: string | null
  addedAt: string
  updatedAt: string
  startedAt: string | null
  completedAt: string | null
  resumePositionPercent: number | null
  /** The most recent scrobble's own progress percent, kept even after the item is marked
   *  Completed (unlike resumePositionPercent, which is cleared then). Null only when the item
   *  has never been scrobbled at all. */
  lastKnownProgressPercent: number | null
  /** Times this item has been watched since its last reset (only set by the single-item
   *  by-media lookup; null in list responses). */
  playCount?: number | null
}

// ── Scrobble ──────────────────────────────────────────────────────────────────
export interface HistoryItem {
  id: number
  mediaItemId: number
  mediaItemName: string
  progressPercent: number | null
  timestamp: string
  markedAsWatched: boolean
  deviceName: string | null
  /** Root-first parent context (e.g. [Show, Season] for an episode) — a scanned TV
   *  episode's own name is often a generic code like "S28E11", meaningless alone. */
  ancestors?: { id: number; name: string }[]
  /** True when `timestamp` is a borrowed fallback (e.g. a SIMKL-imported episode
   *  stamped with its show's last-watched date), not this item's own real watch time. */
  isApproximateTimestamp: boolean
}

/** One currently-live playback session, for the "Now Playing" banner. "Actively playing"
 *  is inferred server-side from scrobble recency — see ScrobbleService.GetActiveSessionsAsync. */
export interface ActiveSession {
  mediaItemId: number
  mediaItemName: string
  posterUrl: string | null
  progressPercent: number
  /** Null when the item has no known runtime — show percentage only. */
  elapsedMinutes: number | null
  runtimeMinutes: number | null
  deviceName: string | null
  lastUpdatedAt: string
  ancestors?: { id: number; name: string }[]
  /** The caller's own 1-10 rating for this item, if set — shown as a badge on the banner. */
  userRating?: number | null
  /** Null for anything that isn't a TV episode — movies have no season/episode to show. */
  season?: number | null
  episode?: number | null
}

// ── Stats ─────────────────────────────────────────────────────────────────────
export interface UserStats {
  totalItemsTracked: number
  totalCompleted: number
  totalWatching: number
  totalScrobbles: number
  totalMinutesWatched: number
  scrobblesThisWeek: number
  scrobblesThisMonth: number
}

// ── Import ────────────────────────────────────────────────────────────────────
export interface ImportProvider {
  pluginId: string
  name: string
  version: string
  description: string
  supportsHistory: boolean
  supportsRatings: boolean
  supportsWatchlist: boolean
  requiresDeviceAuth: boolean
}

export interface ImportAuthStart {
  userCode: string
  verificationUrl: string
  expiresInSeconds: number
  pollingIntervalSeconds: number
  pollCode: string
}

export interface ImportPollResult {
  status: 'pending' | 'authorized' | 'expired' | 'denied'
  errorMessage: string | null
}

export interface ImportResult {
  imported: number
  skipped: number
  errors: string[]
}

export interface SyncResult {
  itemsMatched: number
  stubsCreated: number
  watchEventsAdded: number
  creditsAdded: number
  errors: string[]
}

export interface SyncJobStatus {
  status: 'running' | 'complete' | 'failed'
  summary?: SyncResult
  error?: string
}

// ── File Scanner ──────────────────────────────────────────────────────────────
export interface FileScanStatus {
  available: boolean
  supportedMediaTypeNames: string[]
}

export interface SkippedFile {
  filePath: string
  parsedTitle: string
  confidenceScore: number
}

export interface FileScanResult {
  added: number
  skipped: number
  alreadyInLibrary: number
  skippedFiles: SkippedFile[]
}

export interface ScannedFile {
  filePath: string
  parsedTitle: string
  parsedYear: number | null
  confidenceScore: number
  mediaTypeHint: string
}

export interface ScanPreview {
  files: ScannedFile[]
}

export interface MetadataCandidate {
  externalId: string
  title: string
  year: number | null
  posterUrl: string | null
  overview: string | null
  rating: number | null
  matchScore: number
}

export interface FileIdentification {
  file: ScannedFile
  candidates: MetadataCandidate[]
}

export interface IdentifyResult {
  results: FileIdentification[]
}

export interface ImportSummary {
  imported: number
  failed: number
  failures: string[]
  duplicates: number
}

export interface ScanGroupDto {
  groupKey: string
  name: string
  hierarchyLevel: number
  year: number | null
  number: number | null
  posterPath: string | null
  confidenceScore: number      // 0–100
  signalSources: string[]
  hasConflicts: boolean
  children: ScanGroupDto[]
  files: string[]
  folderPath: string | null
  author: string | null
  series: string | null
  /** Subtitles, artwork, extras found with this group (not importable items themselves). */
  relatedFiles?: string[] | null
  /** Set when the files look like a different media type than the one scanned. */
  suggestedMediaTypeId?: number | null
  suggestedMediaTypeName?: string | null
  suggestedMediaTypeReason?: string | null
  /** For an automatic-detect scan: the media type this group was sorted into. */
  mediaTypeId?: number | null
  mediaTypeName?: string | null
}

export interface ScanGroupResult {
  groups: ScanGroupDto[]
  ungrouped: string[]
  totalFiles: number
  totalGroups: number
}

export interface ImportGroupPayload {
  name: string
  year: number | null
  number: number | null
  posterPath: string | null
  children: ImportGroupPayload[]
  files: string[]
  folderPath: string | null
  relatedFiles?: string[] | null
  /** Set by an automatic-detect scan so each group is imported as its own type. */
  mediaTypeId?: number | null
}

export interface MediaTypeOption {
  id: number
  name: string
  displayName: string
  hierarchyLevels: number
  /** Past-tense action word for this type ("watched", "listened", "read", ...). Words the interface for it. */
  interactionVerb?: string
  /** Names of the hierarchy levels, top first ("Show", "Season", "Episode"). */
  hierarchyLabels?: string[]
  /** True for a type whose top level is a bucket of distinct works (a movie collection). */
  supportsCollections?: boolean
  /** Heading for the people credited on an item of this type ("Band Members", "Narrators"); blank means "Cast". */
  castHeading?: string | null
}

// ── Metadata search ───────────────────────────────────────────────────────────
// Carries the contributing provider's own source alongside its id -- a bare id string is not
// guaranteed unique across different providers' own id spaces (see LibraryItemResolver.cs).
export interface ContributingExternalId {
  source: string
  externalId: string
}

export interface MetadataSearchResult {
  externalId: string
  title: string
  year: number | null
  posterUrl: string | null
  overview: string | null
  rating: number | null
  matchScore: number
  source: string | null
  genres: string[] | null
  cast: string[] | null
  sources: string[] | null
  contributingExternalIds: ContributingExternalId[] | null
  libraryItemId: number | null
}

// ── Scan Folders ──────────────────────────────────────────────────────────────
export interface ScanFolder {
  id: number;
  path: string;
  mediaTypeId: number;
  mediaTypeName: string;
  recursive: boolean;
  isEnabled: boolean;
  createdAt: string;
  lastScannedAt: string | null;
}

// ── API ───────────────────────────────────────────────────────────────────────
export interface ApiResponse<T> {
  success: boolean
  data?: T
  error?: { code: string; message: string }
  pagination?: { page: number; perPage: number; total: number | null }
}
