/**
 * Shared People page display preferences — stored in localStorage, mirroring
 * utils/libraryPrefs.ts's exact pattern (same read/write shape, same synchronous
 * save-on-every-change semantics) per-user request (2026-08-30): "I need the same
 * kinds of controls that the library has. similar filtering and the ability to save."
 *
 * No page-size preset here (Library has one) -- per-user request (2026-08-31): "it
 * keeps repeating that block of 24... limiting the block to few, medium or many is
 * not smart. Can you remove those in favour of just using infinite scroll please?"
 * PeopleLibraryPage now fetches a single fixed page size (PEOPLE_PAGE_SIZE) and grows
 * the list purely via infinite scroll, with nothing user-configurable to key off.
 */

type DeceasedFilter = 'either' | 'living' | 'deceased'

export interface PeopleLibraryPrefs {
  // Single active role filter, '' meaning "no filter" -- a plain string (not a Set)
  // since this has to round-trip through JSON.stringify for localStorage; the page's
  // own role-chip UI already only ever supports one active role at a time (see
  // PeopleLibraryPage's toggleRole/primaryRole).
  role: string
  deceased: DeceasedFilter
}

export const PEOPLE_PREFS_KEY = 'chronicle_people_prefs'

export const DEFAULT_PEOPLE_PREFS: PeopleLibraryPrefs = {
  // Defaults to "Actor" -- per-user request (2026-08-30): a catalog this size reads
  // as noise when writers/directors/crew are all mixed in by default.
  role: 'Actor',
  deceased: 'either',
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
