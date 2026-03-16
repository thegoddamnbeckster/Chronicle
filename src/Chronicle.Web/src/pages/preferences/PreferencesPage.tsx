import { useState } from 'react'
import { useTheme, type Theme } from '@/contexts/ThemeContext'
import { useAuth } from '@/hooks/useAuth'
import { updateMyPreferences } from '@/api/users'
import styles from './PreferencesPage.module.css'

// ── Theme definitions ─────────────────────────────────────────────────────────

interface ThemeDef {
  key: Theme
  label: string
  swatches: [string, string, string]   // [bg, card, accent]
}

const THEMES: ThemeDef[] = [
  {
    key: 'light',
    label: 'Light',
    swatches: ['#f5f5f5', '#e8e8e8', '#6200ea'],
  },
  {
    key: 'dark',
    label: 'Dark',
    swatches: ['#121212', '#2a2a2a', '#bb86fc'],
  },
  {
    key: 'navy-pink',
    label: 'Navy & Pink',
    swatches: ['#1a1a2e', '#0f3460', '#e94560'],
  },
  {
    key: 'dark-teal',
    label: 'Dark Teal',
    swatches: ['#0a2424', '#183c3c', '#00ff88'],
  },
]

// ── Page ──────────────────────────────────────────────────────────────────────

export default function PreferencesPage() {
  const { theme, setTheme } = useTheme()
  const { user, setUser } = useAuth()
  const [diagEnabled, setDiagEnabled] = useState(user?.showDiagnostics ?? false)
  const [diagSaving, setDiagSaving] = useState(false)

  async function handleDiagToggle(value: boolean) {
    setDiagEnabled(value)
    setDiagSaving(true)
    try {
      await updateMyPreferences({ showDiagnostics: value })
      if (user) setUser({ ...user, showDiagnostics: value })
    } catch {
      setDiagEnabled(!value) // revert on error
    } finally {
      setDiagSaving(false)
    }
  }

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Preferences</h1>

      <section className={styles.section}>
        <h2 className={styles.sectionTitle}>Theme</h2>
        <p className={styles.sectionDesc}>Choose the visual style for Chronicle.</p>

        <div className={styles.cards}>
          {THEMES.map(({ key, label, swatches }) => (
            <button
              key={key}
              className={`${styles.card} ${theme === key ? styles.active : ''}`}
              onClick={() => setTheme(key)}
              aria-pressed={theme === key}
            >
              <div className={styles.preview}>
                <div className={styles.swatches}>
                  {swatches.map((color, i) => (
                    <span
                      key={i}
                      className={styles.swatch}
                      style={{ background: color }}
                    />
                  ))}
                </div>
              </div>
              <span className={styles.cardLabel}>{label}</span>
              {theme === key && <span className={styles.activeCheck}>✓</span>}
            </button>
          ))}
        </div>
      </section>

      <section className={styles.section}>
        <h2 className={styles.sectionTitle}>Developer Tools</h2>
        <p className={styles.sectionDesc}>Options for development and debugging.</p>

        <div className={styles.settingRow}>
          <div>
            <div className={styles.settingLabel}>Show Diagnostic Footer</div>
            <div className={styles.settingDesc}>
              Displays a collapsible panel with environment info — useful for debugging.
            </div>
          </div>
          <button
            className={`${styles.toggle} ${diagEnabled ? styles.toggleOn : ''}`}
            onClick={() => handleDiagToggle(!diagEnabled)}
            disabled={diagSaving}
            aria-pressed={diagEnabled}
          >
            {diagEnabled ? 'On' : 'Off'}
          </button>
        </div>
      </section>
    </div>
  )
}
