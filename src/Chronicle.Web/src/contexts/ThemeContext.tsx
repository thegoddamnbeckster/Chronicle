import { createContext, useCallback, useContext, useEffect, useState } from 'react'

// ── Types ─────────────────────────────────────────────────────────────────────

export type Theme = 'light' | 'dark' | 'navy-pink' | 'dark-teal'

export interface ThemeDef {
  key: Theme
  label: string
  description: string
  swatches: [string, string, string]  // [bg, card/mid, accent]
}

/** Single source of truth for all available themes. */
export const THEME_REGISTRY: ThemeDef[] = [
  { key: 'light',      label: 'Light',       description: 'Clean light interface',  swatches: ['#f5f5f5', '#e8e8e8', '#6200ea'] },
  { key: 'dark',       label: 'Dark',         description: 'Dark mode',              swatches: ['#121212', '#2a2a2a', '#bb86fc'] },
  { key: 'navy-pink',  label: 'Navy & Pink',  description: 'Navy base with pink accent', swatches: ['#1a1a2e', '#0f3460', '#e94560'] },
  { key: 'dark-teal',  label: 'Dark Teal',    description: 'Dark teal with green accent', swatches: ['#0a1a1a', '#112e2e', '#00ff88'] },
]

interface ThemeContextValue {
  theme: Theme
  setTheme: (t: Theme) => void
}

const STORAGE_KEY = 'chronicle_theme'
const DEFAULT_THEME: Theme = 'light'

// ── Helpers ───────────────────────────────────────────────────────────────────

function applyTheme(t: Theme) {
  document.documentElement.dataset.theme = t
  localStorage.setItem(STORAGE_KEY, t)
}

function readStoredTheme(): Theme {
  const stored = localStorage.getItem(STORAGE_KEY)
  if (stored && THEME_REGISTRY.some(t => t.key === stored)) return stored as Theme
  return DEFAULT_THEME
}

// ── Context ───────────────────────────────────────────────────────────────────

const ThemeContext = createContext<ThemeContextValue | null>(null)

// ── Provider ──────────────────────────────────────────────────────────────────

export function ThemeProvider({ children }: { children: React.ReactNode }) {
  const [theme, setThemeState] = useState<Theme>(readStoredTheme)

  // Ensure the data-theme attribute is in sync on mount
  useEffect(() => {
    applyTheme(theme)
  }, [theme])

  const setTheme = useCallback((t: Theme) => {
    setThemeState(t)
    applyTheme(t)
  }, [])

  return (
    <ThemeContext.Provider value={{ theme, setTheme }}>
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
