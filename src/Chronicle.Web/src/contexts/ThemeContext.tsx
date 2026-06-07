import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
} from 'react'
import { fetchThemes, type ThemeDto } from '@/api/themes'

// ── Storage keys ───────────────────────────────────────────────────────────────

const KEY_ACTIVE     = 'chronicle_theme'      // stored as "{pluginId}:{key}"
const KEY_VARS_CACHE = 'chronicle_theme_vars' // JSON of Record<string,string>
const DEFAULT_KEY    = 'chronicle.plugin.themes.default:light'

// ── Helpers ───────────────────────────────────────────────────────────────────

/** Apply a variable map to the root element — overrides the base-stylesheet defaults. */
function applyVariables(vars: Record<string, string>) {
  const root = document.documentElement
  // Clear previously applied inline variables first so old overrides don't linger
  // when switching from a theme with more variables to one with fewer.
  for (const prop of Array.from(root.style)) {
    if (prop.startsWith('--')) root.style.removeProperty(prop)
  }
  for (const [name, value] of Object.entries(vars)) {
    root.style.setProperty(name, value)
  }
}

/** Compose the storage key for a theme: "{pluginId}:{themeKey}" */
function themeStorageKey(pluginId: string, key: string) {
  return `${pluginId}:${key}`
}

/**
 * Resolve a (possibly legacy) stored key to a valid theme storage key.
 *
 * Before the plugin-based theme system, Chronicle stored bare theme keys like
 * `"dark"` or `"navy-pink"`. After the migration, keys are `"pluginId:key"`.
 * This helper maps legacy bare keys to their canonical new form so existing
 * user preferences survive the upgrade without any manual reset.
 */
function resolveStorageKey(stored: string, themes: ThemeDto[]): string {
  // Already in the new format — find an exact match.
  if (themes.some(t => themeStorageKey(t.pluginId, t.key) === stored)) return stored

  // Legacy format — find a theme whose `key` segment matches the bare value.
  const legacy = themes.find(t => t.key === stored)
  if (legacy) return themeStorageKey(legacy.pluginId, legacy.key)

  // Unknown / plugin removed — fall back to the default.
  return DEFAULT_KEY
}

// ── Types ─────────────────────────────────────────────────────────────────────

export type { ThemeDto }

interface ThemeContextValue {
  /** All themes from all loaded theme plugins. Empty until the API responds. */
  themes: ThemeDto[]
  /** Storage key of the currently active theme ("{pluginId}:{key}"). */
  activeKey: string
  /** Whether the initial theme list is still loading from the API. */
  loading: boolean
  /** Activate a theme by its storage key. */
  setTheme: (storageKey: string) => void
}

// ── Context ───────────────────────────────────────────────────────────────────

const ThemeContext = createContext<ThemeContextValue | null>(null)

// ── Provider ──────────────────────────────────────────────────────────────────

export function ThemeProvider({ children }: { children: React.ReactNode }) {
  const [themes,    setThemes]    = useState<ThemeDto[]>([])
  const [activeKey, setActiveKey] = useState<string>(
    () => localStorage.getItem(KEY_ACTIVE) ?? DEFAULT_KEY
  )
  const [loading, setLoading] = useState(true)

  // Apply cached variables immediately on mount so there is no flash while the
  // API request is in flight. index.html sets an inline script that does the
  // same thing before React even loads (for absolute zero-flash behaviour).
  useEffect(() => {
    try {
      const cached = localStorage.getItem(KEY_VARS_CACHE)
      if (cached) applyVariables(JSON.parse(cached) as Record<string, string>)
    } catch { /* ignore malformed cache */ }
  }, [])

  // Fetch themes from the API once on mount.
  useEffect(() => {
    let cancelled = false
    void (async () => {
      const fetched = await fetchThemes()
      if (cancelled) return
      setThemes(fetched)
      setLoading(false)

      if (fetched.length === 0) return

      // Resolve saved key — handles legacy bare keys ("dark") and missing themes.
      const rawSaved  = localStorage.getItem(KEY_ACTIVE) ?? DEFAULT_KEY
      const canonical = resolveStorageKey(rawSaved, fetched)
      const match     = fetched.find(t => themeStorageKey(t.pluginId, t.key) === canonical)

      if (!match) return // No theme plugins loaded at all — leave cached vars as-is.

      // Persist the canonical key (upgrades legacy bare keys to new format).
      if (canonical !== rawSaved) {
        localStorage.setItem(KEY_ACTIVE, canonical)
      }
      setActiveKey(canonical)

      // Re-apply using fresh plugin data so variable changes in updated plugins
      // are picked up without requiring the user to manually switch themes.
      applyVariables(match.variables)
      localStorage.setItem(KEY_VARS_CACHE, JSON.stringify(match.variables))
    })()
    return () => { cancelled = true }
  }, [])

  const setTheme = useCallback((storageKey: string) => {
    const match = themes.find(
      t => themeStorageKey(t.pluginId, t.key) === storageKey
    )
    if (!match) return

    setActiveKey(storageKey)
    applyVariables(match.variables)
    localStorage.setItem(KEY_ACTIVE,     storageKey)
    localStorage.setItem(KEY_VARS_CACHE, JSON.stringify(match.variables))
  }, [themes])

  return (
    <ThemeContext.Provider value={{ themes, activeKey, loading, setTheme }}>
      {children}
    </ThemeContext.Provider>
  )
}

// ── Hook ──────────────────────────────────────────────────────────────────────

export function useTheme(): ThemeContextValue {
  const ctx = useContext(ThemeContext)
  if (!ctx) throw new Error('useTheme must be used within ThemeProvider')
  return ctx
}
