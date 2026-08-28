import { useState } from 'react'
import { useTheme } from '@/contexts/ThemeContext'
import { useAuth } from '@/hooks/useAuth'
import { updateMyPreferences } from '@/api/users'
import styles from './PreferencesPage.module.css'

// ── Page ──────────────────────────────────────────────────────────────────────

export default function PreferencesPage() {
  const { themes: availableThemes, activeKey: theme, setTheme } = useTheme()
  const { user, setUser } = useAuth()
  const [diagEnabled, setDiagEnabled] = useState(user?.showDiagnostics ?? false)
  const [diagSaving, setDiagSaving] = useState(false)
  const [nowPlayingEnabled, setNowPlayingEnabled] = useState(user?.showNowPlayingBanner ?? true)
  const [nowPlayingSaving, setNowPlayingSaving] = useState(false)

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

  async function handleNowPlayingToggle(value: boolean) {
    setNowPlayingEnabled(value)
    setNowPlayingSaving(true)
    try {
      await updateMyPreferences({ showNowPlayingBanner: value })
      if (user) setUser({ ...user, showNowPlayingBanner: value })
    } catch {
      setNowPlayingEnabled(!value) // revert on error
    } finally {
      setNowPlayingSaving(false)
    }
  }

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Preferences</h1>

      <section className={styles.section}>
        <h2 className={styles.sectionTitle}>Theme</h2>
        <p className={styles.sectionDesc}>Choose the visual style for Chronicle.</p>

        <div className={styles.cards}>
          {availableThemes.map((t) => {
            const storageKey = `${t.pluginId}:${t.key}`
            const isActive = theme === storageKey
            return (
              <button
                key={storageKey}
                className={`${styles.card} ${isActive ? styles.active : ''}`}
                onClick={() => setTheme(storageKey)}
                aria-pressed={isActive}
              >
                <div className={styles.preview}>
                  <div className={styles.swatches}>
                    {t.swatches.map((color, i) => (
                      <span
                        key={i}
                        className={styles.swatch}
                        style={{ background: color }}
                      />
                    ))}
                  </div>
                </div>
                <span className={styles.cardLabel}>{t.label}</span>
                {isActive && <span className={styles.activeCheck}>✓</span>}
              </button>
            )
          })}
        </div>
      </section>

      <section className={styles.section}>
        <h2 className={styles.sectionTitle}>Now Playing</h2>
        <p className={styles.sectionDesc}>Options for the "Now Playing" banner.</p>

        <div className={styles.settingRow}>
          <div>
            <div className={styles.settingLabel}>Show Now Playing Banner</div>
            <div className={styles.settingDesc}>
              Shows a banner at the top of the page for each device you're actively watching
              something on, with its current progress.
            </div>
          </div>
          <button
            className={`${styles.toggle} ${nowPlayingEnabled ? styles.toggleOn : ''}`}
            onClick={() => handleNowPlayingToggle(!nowPlayingEnabled)}
            disabled={nowPlayingSaving}
            aria-pressed={nowPlayingEnabled}
          >
            {nowPlayingEnabled ? 'On' : 'Off'}
          </button>
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
