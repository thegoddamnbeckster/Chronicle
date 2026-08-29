import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { getHistory } from '@/api/scrobble'
import styles from './HistoryPage.module.css'

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })
}

export default function HistoryPage() {
  const { data: history = [], isLoading } = useQuery({
    queryKey: ['history', 1],
    queryFn: () => getHistory(1),
  })

  return (
    <div className={styles.page}>
      <h2 className={styles.heading}>Watch History</h2>

      {isLoading && <p className={styles.empty}>Loading…</p>}

      {!isLoading && history.length === 0 && (
        <p className={styles.empty}>No scrobbles yet. Start watching something!</p>
      )}

      {history.length > 0 && (
        <div className={styles.tableWrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>Title</th>
                <th>Progress</th>
                <th>Device</th>
                <th>When</th>
                <th>Watched</th>
              </tr>
            </thead>
            <tbody>
              {history.map(h => (
                <tr key={h.id}>
                  <td className={styles.titleCell}>
                    <Link to={`/media/${h.mediaItemId}`} className={styles.titleLink}>
                      {h.ancestors && h.ancestors.length > 0 && (
                        <span className={styles.breadcrumb}>
                          {h.ancestors.map(a => a.name).join(' › ')}
                          {' › '}
                        </span>
                      )}
                      {h.mediaItemName}
                    </Link>
                  </td>
                  <td>{h.progressPercent != null ? `${Math.round(h.progressPercent)}%` : '—'}</td>
                  <td className={styles.meta}>{h.deviceName ?? '—'}</td>
                  <td className={styles.meta}>
                    {h.isApproximateTimestamp ? (
                      <span title="Exact time not available from the source — this is the show's (or item's) last-watched date, not this episode's own.">
                        ~{formatDate(h.timestamp)}
                      </span>
                    ) : (
                      formatDate(h.timestamp)
                    )}
                  </td>
                  <td>
                    {h.markedAsWatched
                      ? <span className={styles.yes}>✓</span>
                      : <span className={styles.no}>—</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
