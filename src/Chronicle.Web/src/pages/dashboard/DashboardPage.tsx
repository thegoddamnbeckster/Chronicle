import { useQuery } from '@tanstack/react-query'
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
import { getHistory } from '@/api/scrobble'
import { getLibrary } from '@/api/library'
import { buildWeeklyActivity } from '@/api/reports'
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
  const { data: history } = useQuery({ queryKey: ['history', 1], queryFn: () => getHistory(1) })
  const { data: watching } = useQuery({
    queryKey: ['library', 'Watching'],
    queryFn: () => getLibrary('Watching'),
  })

  const weeklyData = history ? buildWeeklyActivity(history) : []

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
              {watching.slice(0, 8).map(e => (
                <li key={e.id} className={styles.listItem}>
                  <span className={styles.itemName}>{e.mediaItem.name}</span>
                  <span className={styles.badge}>{e.status}</span>
                </li>
              ))}
            </ul>
          ) : (
            <p className={styles.empty}>Nothing in progress.</p>
          )}
        </section>

        <section className={styles.panel}>
          <h3 className={styles.panelTitle}>Recent Activity</h3>
          {history && history.length > 0 ? (
            <ul className={styles.list}>
              {history.slice(0, 8).map(h => (
                <li key={h.id} className={styles.listItem}>
                  <span className={styles.itemName}>{h.mediaItemName}</span>
                  <span className={styles.meta}>
                    {new Date(h.timestamp).toLocaleDateString()}
                    {h.progressPercent != null && ` · ${Math.round(h.progressPercent)}%`}
                  </span>
                </li>
              ))}
            </ul>
          ) : (
            <p className={styles.empty}>No recent activity.</p>
          )}
        </section>
      </div>
    </div>
  )
}
