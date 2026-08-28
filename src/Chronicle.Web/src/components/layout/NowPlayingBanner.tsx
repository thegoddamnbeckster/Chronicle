import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { useAuth } from '@/hooks/useAuth'
import { getActiveSessions } from '@/api/scrobble'
import { PosterImage } from '@/components/PosterImage'
import styles from './NowPlayingBanner.module.css'

// Polls a bit tighter than the scrobble protocol's 30s default ping interval so the banner
// stays visibly fresh without hammering the API. "Actively playing" is itself inferred
// server-side from a 90s scrobble-recency window (there's no live push/start-stop signal) —
// see ScrobbleService.GetActiveSessionsAsync for the full reasoning.
const POLL_INTERVAL_MS = 20_000

function formatMinutes(minutes: number): string {
  const h = Math.floor(minutes / 60)
  const m = Math.round(minutes % 60)
  return h > 0 ? `${h}h ${m}m` : `${m}m`
}

/**
 * One banner per device currently believed to be actively playing something for the logged-in
 * user — stacked vertically when more than one device is active. Renders nothing at all when
 * no session is active (not an empty placeholder). Sits at the top of the main content area,
 * below the header/search bar and above whatever page is currently displayed, so it's visible
 * from anywhere in the app per the same shell every route renders through.
 */
export default function NowPlayingBanner() {
  const { user } = useAuth()
  // Default true, matching the backend's default when this preference has never been set —
  // see UsersController's `prefs.ShowNowPlayingBanner ?? true`.
  const enabled = user?.showNowPlayingBanner ?? true

  const { data: sessions = [] } = useQuery({
    queryKey: ['active-sessions'],
    queryFn: getActiveSessions,
    refetchInterval: POLL_INTERVAL_MS,
    // Stops polling entirely when the user has turned the banner off, rather than fetching
    // data every 20s just to immediately discard it.
    enabled,
  })

  if (!enabled || sessions.length === 0) return null

  return (
    <div className={styles.stack}>
      {sessions.map(session => {
        // Same breadcrumb shape as api/reports.ts's ancestorBreadcrumb(), inlined rather than
        // imported: that helper was uncommitted, unstable working-tree state elsewhere in the
        // repo at the time this was written, not something worth taking a hard dependency on.
        const context = session.ancestors && session.ancestors.length > 0
          ? session.ancestors.map(a => a.name).join(' › ')
          : null
        const percent = Math.max(0, Math.min(100, session.progressPercent))
        const timeText = session.elapsedMinutes != null && session.runtimeMinutes != null
          ? `${formatMinutes(session.elapsedMinutes)} / ${formatMinutes(session.runtimeMinutes)}`
          : null

        return (
          <Link
            // deviceName, not mediaItemId, is the stable per-row identity — two devices can
            // legitimately be watching the same item at once (e.g. a rewatch on two TVs).
            key={session.deviceName ?? `item-${session.mediaItemId}`}
            to={`/media/${session.mediaItemId}`}
            className={styles.banner}
          >
            <PosterImage
              posterUrl={session.posterUrl}
              name={session.mediaItemName}
              className={styles.poster}
            />
            <div className={styles.info}>
              <div className={styles.titleRow}>
                {context && <span className={styles.context}>{context}</span>}
                <span className={styles.title}>{session.mediaItemName}</span>
              </div>
              <div className={styles.deviceRow}>
                <span className={styles.device}>{session.deviceName ?? 'Unknown device'}</span>
                <span className={styles.percent}>
                  {Math.round(percent)}%{timeText ? ` · ${timeText}` : ''}
                </span>
              </div>
              <div className={styles.progressTrack}>
                <div className={styles.progressFill} style={{ width: `${percent}%` }} />
              </div>
            </div>
          </Link>
        )
      })}
    </div>
  )
}
