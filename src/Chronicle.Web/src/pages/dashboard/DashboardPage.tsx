import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from 'recharts'
import { getStats } from '@/api/stats'
import { getLibrary } from '@/api/library'
import { ancestorBreadcrumb, buildWeeklyActivity, dedupeHistoryByMediaItem, getHistoryPage } from '@/api/reports'
import styles from './DashboardPage.module.css'

function StatCard({ label, value }: { label: string; value: string | number }) {
  return (
    <div className={styles.statCard}>
      <div className={styles.statValue}>{value}</div>
      <div className={styles.statLabel}>{label}</div>
    </div>
  )
}

function formatMinutes(mins: number): string {
  const h = Math.floor(mins / 60)
  const d = Math.floor(h / 24)
  if (d > 0) return `${d}d ${h % 24}h`
  return `${h}h ${mins % 60}m`
}

export default function DashboardPage() {
  const { data: stats } = useQuery({ queryKey: ['stats'], queryFn: getStats })
  // Fetched wide (100 raw pings) so that deduping down to one row per media
  // item below still leaves a full, non-starved "Recent Activity" list.
  const { data: history } = useQuery({
    queryKey: ['history', 'dashboard'],
    queryFn: () => getHistoryPage(1, 100),
  })
  const { data: watching } = useQuery({
    queryKey: ['library', 'Watching'],
    queryFn: () => getLibrary('Watching'),
  })

  const weeklyData = history ? buildWeeklyActivity(history) : []
  const recentActivity = history ? dedupeHistoryByMediaItem(history) : []

  return (
    <div className={styles.page}>
      <h2 className={styles.heading}>Dashboard</h2>

      {stats && (
        <div className={styles.statsGrid}>
          <StatCard label="Tracked" value={stats.totalItemsTracked} />
          <StatCard label="Watching" value={stats.totalWatching} />
          <StatCard label="Completed" value={stats.totalCompleted} />
          <StatCard label="This Week" value={stats.scrobblesThisWeek} />
          <StatCard label="This Month" value={stats.scrobblesThisMonth} />
          <StatCard label="Watch Time" value={formatMinutes(stats.totalMinutesWatched)} />
        </div>
      )}

      {/* 7-day activity chart */}
      {weeklyData.length > 0 && (
        <div className={styles.chartPanel}>
          <h3 className={styles.panelTitle}>Activity — Last 7 Days</h3>
          <ResponsiveContainer width="100%" height={160}>
            <AreaChart data={weeklyData} margin={{ top: 4, right: 0, left: -20, bottom: 0 }}>
              <defs>
                <linearGradient id="actGrad" x1="0" y1="0" x2="0" y2="1">
                  <stop offset="5%" stopColor="var(--accent)" stopOpacity={0.3} />
                  <stop offset="95%" stopColor="var(--accent)" stopOpacity={0} />
                </linearGradient>
              </defs>
              <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
              <XAxis
                dataKey="day"
                tick={{ fill: 'var(--text-muted)', fontSize: 11 }}
                axisLine={false}
                tickLine={false}
              />
              <YAxis
                allowDecimals={false}
                tick={{ fill: 'var(--text-muted)', fontSize: 11 }}
                axisLine={false}
                tickLine={false}
              />
              <Tooltip
                contentStyle={{
                  background: 'var(--bg-secondary)',
                  border: '1px solid var(--border)',
                  borderRadius: 6,
                  fontSize: 12,
                }}
              />
              <Area
                type="monotone"
                dataKey="count"
                name="Scrobbles"
                stroke="var(--accent)"
                fill="url(#actGrad)"
                strokeWidth={2}
                dot={false}
              />
            </AreaChart>
          </ResponsiveContainer>
        </div>
      )}

      <div className={styles.panels}>
        <section className={styles.panel}>
          <h3 className={styles.panelTitle}>Continue Watching</h3>
          {watching && watching.length > 0 ? (
            <ul className={styles.list}>
              {watching.slice(0, 8).map(e => {
                const context = ancestorBreadcrumb(e.mediaItem.ancestors)
                const percent = e.resumePositionPercent
                return (
                  <li key={e.id} className={styles.listItem}>
                    <div className={styles.itemNameCol}>
                      <Link to={`/media/${e.mediaItem.id}`} className={styles.itemName}>
                        {e.mediaItem.name}
                      </Link>
                      {context && <span className={styles.itemContext}>{context}</span>}
                    </div>
                    <div className={styles.rightCol}>
                      <span className={styles.badge}>{e.status}</span>
                      <span className={styles.timestamp}>
                        {new Date(e.updatedAt).toLocaleString(undefined, {
                          dateStyle: 'short', timeStyle: 'short',
                        })}
                        {percent != null && ` · ${Math.round(percent)}%`}
                      </span>
                    </div>
                  </li>
                )
              })}
            </ul>
          ) : (
            <p className={styles.empty}>Nothing in progress.</p>
          )}
        </section>

        <section className={styles.panel}>
          <h3 className={styles.panelTitle}>Recent Activity</h3>
          {recentActivity.length > 0 ? (
            <ul className={styles.list}>
              {recentActivity.slice(0, 8).map(h => {
                const context = ancestorBreadcrumb(h.ancestors)
                return (
                  <li key={h.id} className={styles.listItem}>
                    <div className={styles.itemNameCol}>
                      <Link to={`/media/${h.mediaItemId}`} className={styles.itemName}>
                        {h.mediaItemName}
                      </Link>
                      {context && <span className={styles.itemContext}>{context}</span>}
                    </div>
                    <span
                      className={styles.meta}
                      title={h.isApproximateTimestamp
                        ? "Exact time not available from the source — this is the show's (or item's) last-watched date, not this episode's own."
                        : undefined}
                    >
                      {h.isApproximateTimestamp && '~'}
                      {new Date(h.timestamp).toLocaleString(undefined, {
                        dateStyle: 'short', timeStyle: 'short',
                      })}
                      {h.progressPercent != null && ` · ${Math.round(h.progressPercent)}%`}
                    </span>
                  </li>
                )
              })}
            </ul>
          ) : (
            <p className={styles.empty}>No recent activity.</p>
          )}
        </section>
      </div>
    </div>
  )
}
