// ── Auth ──────────────────────────────────────────────────────────────────────
export interface User {
  id: number
  username: string
  email: string | null
  displayName: string | null
  isAdmin: boolean
  showDiagnostics: boolean
  showNowPlayingBanner: boolean
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
  nfoPosterUrl: string | null
  importedAt: string | null
  nfoPath: string | null
}

export interface NfoActor {
  name: string | null
  role: string | null
}

export interface NfoDetail {
  title: string | null
  originalTitle: string | null
  plot: string | null
  genres: string[]
  rating: number | null
  mpaa: string | null
  studio: string | null
  runtimeMinutes: number | null
  premiered: string | null
  director: string | null
  writers: string[]
  actors: NfoActor[]
  collectionName: string | null
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
  /** Promoted canonical fields for mediaTypeName == "people" -- null for every other type. */
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
}

export interface PersonCredit {
  mediaItemId: number
  name: string
  posterUrl: string | null
  year: number | null
  mediaTypeName: string
  characterName: string | null
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
  suggestedExternalId: string | null
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
  nfoPath: string | null
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
  nfoPath: string | null
}

export interface MediaTypeOption {
  id: number
  name: string
  displayName: string
  hierarchyLevels: number
}

// ── Metadata search ───────────────────────────────────────────────────────────
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
  contributingExternalIds: string[] | null
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
