// ── Auth ──────────────────────────────────────────────────────────────────────
export interface User {
  id: number
  username: string
  email: string | null
  displayName: string | null
  isAdmin: boolean
  showDiagnostics: boolean
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

export interface LibraryEntry {
  id: number
  userId: number
  mediaItem: MediaItem
  status: LibraryStatus
  userRating: number | null
  notes: string | null
  addedAt: string
  updatedAt: string
  startedAt: string | null
  completedAt: string | null
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
}

export interface MediaTypeOption {
  id: number
  name: string
  displayName: string
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
