import { useCallback, useEffect, useState } from 'react'
import type { SessionInfo } from '@/types'
import styles from '@/pages/settings/UsersPage.module.css'

interface Props {
  /** Loads the sessions to show. */
  load: () => Promise<SessionInfo[]>
  /** Ends one session. Omit to hide the per-row button (admin view ends all at once). */
  onEnd?: (id: string) => Promise<void>
  /** Ends every session shown (and, for the self view, signs this browser out too). */
  onEndAll?: { label: string; run: () => Promise<void>; confirm: string }
  emptyText?: string
}

/** Short "Chrome on Windows"-style label from a user-agent string, falling back to the raw text. */
function describeAgent(ua: string | null): string {
  if (!ua) return 'Unknown device'
  const browser =
    /Edg\//.test(ua) ? 'Edge' :
    /Firefox\//.test(ua) ? 'Firefox' :
    /Chrome\//.test(ua) ? 'Chrome' :
    /Safari\//.test(ua) ? 'Safari' : null
  const os =
    /Windows/.test(ua) ? 'Windows' :
    /Android/.test(ua) ? 'Android' :
    /iPhone|iPad/.test(ua) ? 'iOS' :
    /Mac OS X/.test(ua) ? 'macOS' :
    /Linux/.test(ua) ? 'Linux' : null
  return browser && os ? `${browser} on ${os}` : (browser ?? os ?? ua.slice(0, 60))
}

function when(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString()
}

export default function SessionsList({ load, onEnd, onEndAll, emptyText = 'No active sessions.' }: Props) {
  const [sessions, setSessions] = useState<SessionInfo[] | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  const refresh = useCallback(async () => {
    try {
      setSessions(await load())
      setError('')
    } catch {
      setError('Could not load sessions.')
    }
  }, [load])

  useEffect(() => { void refresh() }, [refresh])

  async function run(action: () => Promise<void>) {
    setBusy(true)
    setError('')
    try {
      await action()
      await refresh()
    } catch {
      setError('That did not work.')
    } finally {
      setBusy(false)
    }
  }

  if (sessions === null) return <p className={styles.hint}>{error || 'Loading…'}</p>

  return (
    <div>
      {sessions.length === 0 && <p className={styles.hint}>{emptyText}</p>}
      {sessions.map(s => (
        <div key={s.id} className={styles.userRow}>
          <div className={styles.userInfo}>
            <span className={styles.userName}>
              {describeAgent(s.userAgent)}{s.isCurrent ? ' (this session)' : ''}
            </span>
            <span className={styles.userMeta}>
              {s.remoteIp ?? 'unknown address'} · signed in {when(s.createdAt)} · last active {when(s.lastSeenAt)}
            </span>
          </div>
          {onEnd && !s.isCurrent && (
            <div className={styles.userActions}>
              <button className={styles.smallBtn} disabled={busy}
                      onClick={() => void run(() => onEnd(s.id))}>
                End session
              </button>
            </div>
          )}
        </div>
      ))}
      {onEndAll && sessions.length > 0 && (
        <button className={styles.dangerBtn} disabled={busy}
                onClick={() => { if (window.confirm(onEndAll.confirm)) void run(onEndAll.run) }}>
          {onEndAll.label}
        </button>
      )}
      {error && <p className={styles.error}>{error}</p>}
    </div>
  )
}
