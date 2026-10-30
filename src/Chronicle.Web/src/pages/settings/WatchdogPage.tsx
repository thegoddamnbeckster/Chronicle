import { useCallback, useEffect, useState } from 'react'
import { getWatchdog, setWatchdogEnabled, type WatchdogStatus } from '@/api/watchdog'
import styles from './UsersPage.module.css'

const REFRESH_MS = 10_000

function message(err: unknown, fallback: string): string {
  return err instanceof Error && err.message ? err.message : fallback
}

function when(utc: string | null): string {
  return utc ? new Date(utc).toLocaleString() : 'never'
}

/** Keeps Chronicle running without anyone managing it: a small background helper restarts it if it stops answering. */
export default function WatchdogPage() {
  const [status, setStatus] = useState<WatchdogStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const refresh = useCallback(() => {
    getWatchdog().then(setStatus).catch(err => setError(message(err, 'Could not load the watchdog status.')))
  }, [])

  useEffect(() => {
    refresh()
    const timer = setInterval(refresh, REFRESH_MS)
    return () => clearInterval(timer)
  }, [refresh])

  async function toggle(enabled: boolean) {
    setBusy(true); setError('')
    try {
      setStatus(await setWatchdogEnabled(enabled))
    } catch (err) {
      setError(message(err, 'Could not change the watchdog.'))
    } finally {
      setBusy(false)
    }
  }

  if (!status) {
    return <div className={styles.page}><h1 className={styles.title}>Watchdog</h1>{error ? <p className={styles.error}>{error}</p> : <p className={styles.loading}>Loading…</p>}</div>
  }

  const problem = status.enabled && ['gave-up', 'error', 'not-running'].includes(status.state)

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Watchdog</h1>
      <p className={styles.hint}>
        Keeps Chronicle running on its own. A small background program (Chronicle.Watchdog) checks every 10 seconds that
        Chronicle is answering and is using its real database, and starts it again if it is not. It starts with
        Windows and never opens a window. Stopping Chronicle on purpose is respected: it is not started again.
      </p>
      {error && <p className={styles.error} role="alert">{error}</p>}
      {!status.supported && <p className={styles.hint}>{status.unsupportedReason}</p>}

      <div className={styles.card}>
        <h2 className={styles.cardTitle}>Keep Chronicle running</h2>
        <div className={styles.formRow}>
          <div className={styles.formGroup}>
            <label className={styles.label} htmlFor="watchdog-enabled">
              <input
                id="watchdog-enabled" type="checkbox" checked={status.enabled}
                disabled={busy || (!status.supported && !status.enabled)}
                onChange={e => toggle(e.target.checked)}
              />
              {' '}Restart Chronicle automatically if it stops working
            </label>
          </div>
        </div>
        {status.enabled && (
          <>
            <p className={problem ? styles.error : styles.success} role="status">
              {status.message || 'Starting…'}
            </p>
            <p className={styles.hint}>
              Last checked: {when(status.lastCheckUtc)}. Last automatic restart: {when(status.lastRestartUtc)}.
            </p>
          </>
        )}
      </div>

      {status.enabled && status.events.length > 0 && (
        <div className={styles.card}>
          <h2 className={styles.cardTitle}>Recent activity</h2>
          <ul>
            {status.events.map((e, i) => (
              <li key={`${e.timeUtc}-${i}`}>{when(e.timeUtc)}: {e.message}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
