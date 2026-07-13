// ── Sort Settings ─────────────────────────────────────────────────────────────
// Stored in localStorage so the setting persists across sessions.
// Default: article-ignoring is OFF.

const SORT_SETTINGS_KEY = 'chronicle_sort_settings'

export interface SortSettings {
  ignoreArticles: boolean
  ignoredArticles: string[]
}

/** Common leading articles across English, French, Spanish, German, Italian, and Portuguese. */
export const DEFAULT_IGNORED_ARTICLES: string[] = [
  // English
  'a', 'an', 'the',
  // French
  'le', 'la', 'les', "l'", 'un', 'une', 'des',
  // Spanish
  'el', 'los', 'las', 'lo', 'un', 'una', 'unos', 'unas',
  // German
  'der', 'die', 'das', 'ein', 'eine',
  // Italian
  'il', 'gli', "l'",
  // Portuguese
  'o', 'os', 'um', 'uma',
]

// Remove duplicates (e.g. 'la' appears in French, Spanish, Italian)
const UNIQUE_DEFAULT_ARTICLES = [...new Set(DEFAULT_IGNORED_ARTICLES)]

export const DEFAULT_SORT_SETTINGS: SortSettings = {
  ignoreArticles: false,
  ignoredArticles: UNIQUE_DEFAULT_ARTICLES,
}

export function loadSortSettings(): SortSettings {
  try {
    const raw = localStorage.getItem(SORT_SETTINGS_KEY)
    if (raw) return { ...DEFAULT_SORT_SETTINGS, ...JSON.parse(raw) }
  } catch {
    // ignore parse errors
  }
  return { ...DEFAULT_SORT_SETTINGS }
}

export function saveSortSettings(settings: SortSettings): void {
  localStorage.setItem(SORT_SETTINGS_KEY, JSON.stringify(settings))
}

/**
 * Strips a leading article from the name for sorting purposes.
 * e.g. "The Dark Knight" → "Dark Knight" when "the" is in the ignored list.
 */
export function stripLeadingArticle(name: string, articles: string[]): string {
  const lower = name.toLowerCase()
  for (const article of articles) {
    const prefix = article.toLowerCase() + ' '
    if (lower.startsWith(prefix)) {
      return name.slice(prefix.length)
    }
  }
  return name
}

/**
 * Returns the single character an alphabetical index (or scroll-position indicator)
 * should group `name` under — the same character its name-sort key starts with, so
 * this always agrees with how the list is actually ordered. Non-letters (numbers,
 * symbols) bucket under '#', matching the usual A–Z index convention.
 */
export function getIndexLetter(name: string, settings: SortSettings): string {
  const key = settings.ignoreArticles ? stripLeadingArticle(name, settings.ignoredArticles) : name
  const ch = key.trim().charAt(0).toUpperCase()
  return ch >= 'A' && ch <= 'Z' ? ch : '#'
}
