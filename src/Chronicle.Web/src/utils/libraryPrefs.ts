/**
 * Shared library display preferences — stored in localStorage.
 * Imported by both LibraryPage and LibrarySettingsPage to avoid a
 * cross-page dependency that would pull the full library bundle into settings.
 */

import type { LibraryStatus } from '@/types'

type SortField = 'name' | 'year' | 'dateAdded' | 'rating' | 'status'
type SortDir = 'asc' | 'desc'
type PageSizePreset = 'minimal' | 'medium' | 'maximal' | 'all'

export interface LibraryPrefs {
  sortBy: SortField
  sortDir: SortDir
  statusFilter?: LibraryStatus
  pageSizePreset: PageSizePreset
  groupMoviesIntoCollections: boolean
}

export const PREFS_KEY = 'chronicle_library_prefs'

export const DEFAULT_PREFS: LibraryPrefs = {
  sortBy: 'name',
  sortDir: 'asc',
  statusFilter: undefined,
  pageSizePreset: 'medium',
  groupMoviesIntoCollections: true,
}

export function loadPrefs(): LibraryPrefs {
  try {
    const raw = localStorage.getItem(PREFS_KEY)
    if (raw) return { ...DEFAULT_PREFS, ...JSON.parse(raw) }
  } catch { /* ignore */ }
  return { ...DEFAULT_PREFS }
}

export function savePrefs(prefs: LibraryPrefs) {
  localStorage.setItem(PREFS_KEY, JSON.stringify(prefs))
}
