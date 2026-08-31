/**
 * Shared People page display preferences — stored in localStorage, mirroring
 * utils/libraryPrefs.ts's exact pattern (same read/write shape, same synchronous
 * save-on-every-change semantics) per-user request (2026-08-30): "I need the same
 * kinds of controls that the library has. similar filtering and the ability to save."
 *
 * "All" is deliberately NOT one of the page-size presets here the way it is on
 * Library's PAGE_SIZES: Library's "All" is nearly free because it slices an
 * already-fully-fetched, typically small in-memory array. The People catalog is
 * two orders of magnitude larger (183k+ records, 32k+ even filtered to one role) --
 * fetching and rendering "all" of it as a single page would hang the tab. Per-user
 * decision (2026-08-30, asked directly): drop "All", keep Few/Medium/Many, each
 * still paginating for real via Load More (see PeopleLibraryPage's useInfiniteQuery).
 */

type SortOption = 'name' | 'birthDate' | 'createdAt'
type DeceasedFilter = 'either' | 'living' | 'deceased'
type PageSizePreset = 'minimal' | 'medium' | 'maximal'

export interface PeopleLibraryPrefs {
  sort: SortOption
  // Single active role filter, '' meaning "no filter" -- a plain string (not a Set)
  // since this has to round-trip through JSON.stringify for localStorage; the page's
  // own role-chip UI already only ever supports one active role at a time (see
  // PeopleLibraryPage's toggleRole/primaryRole).
  role: string
  deceased: DeceasedFilter
  pageSizePreset: PageSizePreset
}

export const PEOPLE_PAGE_SIZES: Record<PageSizePreset, number> = {
  minimal: 6,
  medium: 24,
  maximal: 100,
}

export const PEOPLE_PREFS_KEY = 'chronicle_people_prefs'

export const DEFAULT_PEOPLE_PREFS: PeopleLibraryPrefs = {
  sort: 'name',
  // Defaults to "Actor" -- per-user request (2026-08-30): a catalog this size reads
  // as noise when writers/directors/crew are all mixed in by default.
  role: 'Actor',
  deceased: 'either',
  pageSizePreset: 'medium',
}

export function loadPeoplePrefs(): PeopleLibraryPrefs {
  try {
    const raw = localStorage.getItem(PEOPLE_PREFS_KEY)
    if (raw) return { ...DEFAULT_PEOPLE_PREFS, ...JSON.parse(raw) }
  } catch { /* ignore */ }
  return { ...DEFAULT_PEOPLE_PREFS }
}

export function savePeoplePrefs(prefs: PeopleLibraryPrefs) {
  localStorage.setItem(PEOPLE_PREFS_KEY, JSON.stringify(prefs))
}
