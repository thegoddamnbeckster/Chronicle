import { useQuery } from '@tanstack/react-query'
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
                  {h.ancestors && h.ancestors.length > 0 && (
                    <span className={styles.ancestors}>
                      {h.ancestors.map(a => a.name).join(' › ')} ›{' '}
                    </span>
                  )}
                  {h.mediaItemName}
                </td>
                <td>{h.progressPercent != null ? `${Math.round(h.progressPercent)}%` : '—'}</td>
                <td className={styles.meta}>{h.deviceName ?? '—'}</td>
                <td className={styles.meta}>
                  {h.timestampIsApproximate && (
                    <span
                      className={styles.approx}
                      title="Exact time not reported by the source (e.g. a whole show bulk-marked complete shares one date across every episode) — this is not this item's own precise watch time."
                    >
                      ~{' '}
                    </span>
                  )}
                  {formatDate(h.timestamp)}
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
      )}
    </div>
  )
}
