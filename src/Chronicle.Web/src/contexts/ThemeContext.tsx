import { createContext, useCallback, useContext, useEffect, useState } from 'react'

// ── Types ─────────────────────────────────────────────────────────────────────

export type Theme = 'light' | 'dark' | 'navy-pink' | 'dark-teal'

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
  if (stored === 'light' || stored === 'dark' || stored === 'navy-pink' || stored === 'dark-teal') return stored
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
