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

      // Re-apply the active theme using fresh data (picks up any variable changes
      // from an updated plugin without requiring the user to switch themes).
      const savedKey = localStorage.getItem(KEY_ACTIVE) ?? DEFAULT_KEY
      const match = fetched.find(
        t => themeStorageKey(t.pluginId, t.key) === savedKey
      )
      if (match) {
        applyVariables(match.variables)
        localStorage.setItem(KEY_VARS_CACHE, JSON.stringify(match.variables))
      }
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
