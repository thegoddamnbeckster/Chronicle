import { useTheme, type Theme } from '@/contexts/ThemeContext'
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
]

// ── Page ──────────────────────────────────────────────────────────────────────

export default function PreferencesPage() {
  const { theme, setTheme } = useTheme()

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
    </div>
  )
}
