import { useState } from 'react'
import { useTheme } from '@/contexts/ThemeContext'
import { useAuth } from '@/hooks/useAuth'
import { useEffect } from 'react'
import { getMyPreferences, updateMyPreferences } from '@/api/users'
import { listNotificationKinds, type NotificationKindInfo } from '@/api/notifications'
import styles from './PreferencesPage.module.css'

// ── Page ──────────────────────────────────────────────────────────────────────

export default function PreferencesPage() {
  const { themes: availableThemes, activeKey: theme, setTheme } = useTheme()
  const { user, setUser } = useAuth()
  const [diagEnabled, setDiagEnabled] = useState(user?.showDiagnostics ?? false)
  const [diagSaving, setDiagSaving] = useState(false)
  const [nowPlayingEnabled, setNowPlayingEnabled] = useState(user?.showNowPlayingBanner ?? true)
  const [nowPlayingSaving, setNowPlayingSaving] = useState(false)
  const [allCreditsEnabled, setAllCreditsEnabled] = useState(user?.showAllCredits ?? false)
  const [allCreditsSaving, setAllCreditsSaving] = useState(false)

  const [kinds, setKinds] = useState<NotificationKindInfo[]>([])
  const [muted, setMuted] = useState<string[]>([])

  useEffect(() => {
    let alive = true
    Promise.all([listNotificationKinds(), getMyPreferences()])
      .then(([k, prefs]) => { if (alive) { setKinds(k); setMuted(prefs.mutedNotificationKinds ?? []) } })
      .catch(() => { /* the section just stays empty */ })
    return () => { alive = false }
  }, [])

  async function handleKindToggle(kind: string, wanted: boolean) {
    const next = wanted ? muted.filter(k => k !== kind) : [...muted, kind]
    const before = muted
    setMuted(next)
    try {
      await updateMyPreferences({ mutedNotificationKinds: next })
    } catch {
      setMuted(before) // revert on error
    }
  }

  async function handleAllCreditsToggle(value: boolean) {
    setAllCreditsEnabled(value)
    setAllCreditsSaving(true)
    try {
      await updateMyPreferences({ showAllCredits: value })
      if (user) setUser({ ...user, showAllCredits: value })
    } catch {
      setAllCreditsEnabled(!value) // revert on error
    } finally {
      setAllCreditsSaving(false)
    }
  }

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

      {kinds.length > 0 && (
        <section className={styles.section}>
          <h2 className={styles.sectionTitle}>Notifications</h2>
          <p className={styles.sectionDesc}>Choose what shows up under the bell.</p>
          {kinds.map(k => (
            <div key={k.kind} className={styles.settingRow}>
              <label>
                <input type="checkbox" checked={!muted.includes(k.kind)}
                       onChange={e => void handleKindToggle(k.kind, e.target.checked)} />
                {' '}{k.label}
              </label>
              <span className={styles.sectionDesc}>{k.description}</span>
            </div>
          ))}
        </section>
      )}

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
        <h2 className={styles.sectionTitle}>People</h2>
        <p className={styles.sectionDesc}>Options for person pages.</p>

        <div className={styles.settingRow}>
          <div>
            <div className={styles.settingLabel}>Show every credit by default</div>
            <div className={styles.settingDesc}>
              Person pages list every credit the person has ever had, including titles that are not in
              your library. Changing this applies to every person page; the toggle on a person page only
              changes that view.
            </div>
          </div>
          <button
            className={`${styles.toggle} ${allCreditsEnabled ? styles.toggleOn : ''}`}
            onClick={() => handleAllCreditsToggle(!allCreditsEnabled)}
            disabled={allCreditsSaving}
            aria-pressed={allCreditsEnabled}
          >
            {allCreditsEnabled ? 'On' : 'Off'}
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
