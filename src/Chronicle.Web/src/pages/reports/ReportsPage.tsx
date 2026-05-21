import { useQuery } from '@tanstack/react-query'
import {
  AreaChart,
  Area,
  BarChart,
  Bar,
  PieChart,
  Pie,
  Cell,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from 'recharts'
import {
  getStats,
  getHistoryPage,
  getLibraryPage,
  buildMonthlyActivity,
  buildStatusBreakdown,
} from '@/api/reports'
import { getLibraryStats } from '@/api/stats'
import { getDiagnostics } from '@/api/diagnostics'
import styles from './ReportsPage.module.css'

// Accent palette for pie/bar charts
const PALETTE = ['#e94560', '#3498db', '#2ecc71', '#f39c12', '#9b59b6', '#1abc9c']

function formatMinutes(mins: number): string {
  const h = Math.floor(mins / 60)
  const d = Math.floor(h / 24)
  if (d > 0) return `${d}d ${h % 24}h`
  return `${h}h ${mins % 60}m`
}

// ── Shared chart theme ────────────────────────────────────────────────────────

const TOOLTIP_STYLE = {
  background: 'var(--bg-secondary)',
  border: '1px solid var(--border)',
  borderRadius: 6,
  fontSize: 12,
  color: 'var(--text-primary)',
}

const TICK = { fill: 'var(--text-muted)', fontSize: 11 }

// ── Sub-components ────────────────────────────────────────────────────────────

function SectionTitle({ children }: { children: React.ReactNode }) {
  return <h3 className={styles.sectionTitle}>{children}</h3>
}

function ChartCard({
  title,
  children,
}: {
  title: string
  children: React.ReactNode
}) {
  return (
    <div className={styles.chartCard}>
      <div className={styles.chartTitle}>{title}</div>
      {children}
    </div>
  )
}

// ── Page ──────────────────────────────────────────────────────────────────────

export default function ReportsPage() {
  const { data: stats } = useQuery({ queryKey: ['stats'], queryFn: getStats })

  const { data: history = [] } = useQuery({
    queryKey: ['history-report'],
    queryFn: () => getHistoryPage(1, 200),
  })

  const { data: library = [] } = useQuery({
    queryKey: ['library-report'],
    queryFn: () => getLibraryPage(),
  })

  const { data: libStats } = useQuery({
    queryKey: ['library-stats'],
    queryFn: getLibraryStats,
    staleTime: 60_000,
  })

  const { data: diag } = useQuery({
    queryKey: ['diagnostics-report'],
    queryFn: getDiagnostics,
    staleTime: 60_000,
  })

  const monthlyData = buildMonthlyActivity(history)
  const statusData  = buildStatusBreakdown(library)

  // Weekly average
  const totalThisMonth = monthlyData.reduce((s, d) => s + d.count, 0)
  const weeklyAvg = Math.round(totalThisMonth / 4)

  return (
    <div className={styles.page}>
      <h2 className={styles.heading}>Reports</h2>
      <p className={styles.subtitle}>
        A summary of your Chronicle activity. Data is computed from your library and scrobble history.
      </p>

      {/* ── KPI row ── */}
      {stats && (
        <div className={styles.kpiRow}>
          <div className={styles.kpi}>
            <div className={styles.kpiValue}>{stats.totalItemsTracked}</div>
            <div className={styles.kpiLabel}>Total Tracked</div>
          </div>
          <div className={styles.kpi}>
            <div className={styles.kpiValue}>{stats.totalCompleted}</div>
            <div className={styles.kpiLabel}>Completed</div>
          </div>
          <div className={styles.kpi}>
            <div className={styles.kpiValue}>{stats.totalScrobbles}</div>
            <div className={styles.kpiLabel}>Total Scrobbles</div>
          </div>
          <div className={styles.kpi}>
            <div className={styles.kpiValue}>{formatMinutes(stats.totalMinutesWatched)}</div>
            <div className={styles.kpiLabel}>Total Watch Time</div>
          </div>
          <div className={styles.kpi}>
            <div className={styles.kpiValue}>{stats.scrobblesThisWeek}</div>
            <div className={styles.kpiLabel}>This Week</div>
          </div>
          <div className={styles.kpi}>
            <div className={styles.kpiValue}>{weeklyAvg}</div>
            <div className={styles.kpiLabel}>Weekly Avg (30d)</div>
          </div>
        </div>
      )}

      {/* ── Activity charts ── */}
      <SectionTitle>Activity</SectionTitle>
      <div className={styles.chartGrid}>
        <ChartCard title="Daily Scrobbles — Last 30 Days">
          {monthlyData.length > 0 ? (
            <ResponsiveContainer width="100%" height={220}>
              <AreaChart data={monthlyData} margin={{ top: 4, right: 0, left: -24, bottom: 0 }}>
                <defs>
                  <linearGradient id="monthGrad" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="5%" stopColor="#e94560" stopOpacity={0.3} />
                    <stop offset="95%" stopColor="#e94560" stopOpacity={0} />
                  </linearGradient>
                </defs>
                <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
                <XAxis
                  dataKey="day"
                  tick={TICK}
                  axisLine={false}
                  tickLine={false}
                  interval={6}
                />
                <YAxis allowDecimals={false} tick={TICK} axisLine={false} tickLine={false} />
                <Tooltip contentStyle={TOOLTIP_STYLE} />
                <Area
                  type="monotone"
                  dataKey="count"
                  name="Scrobbles"
                  stroke="#e94560"
                  fill="url(#monthGrad)"
                  strokeWidth={2}
                  dot={false}
                />
              </AreaChart>
            </ResponsiveContainer>
          ) : (
            <p className={styles.noData}>No scrobble data yet.</p>
          )}
        </ChartCard>

        <ChartCard title="This Week vs Last Week">
          {stats ? (
            <ResponsiveContainer width="100%" height={220}>
              <BarChart
                data={[
                  { period: 'Last Week', count: Math.max(0, stats.scrobblesThisMonth - stats.scrobblesThisWeek) },
                  { period: 'This Week', count: stats.scrobblesThisWeek },
                ]}
                margin={{ top: 4, right: 0, left: -24, bottom: 0 }}
              >
                <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" />
                <XAxis dataKey="period" tick={TICK} axisLine={false} tickLine={false} />
                <YAxis allowDecimals={false} tick={TICK} axisLine={false} tickLine={false} />
                <Tooltip contentStyle={TOOLTIP_STYLE} />
                <Bar dataKey="count" name="Scrobbles" fill="#e94560" radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          ) : (
            <p className={styles.noData}>No data.</p>
          )}
        </ChartCard>
      </div>

      {/* ── Library charts ── */}
      <SectionTitle>Library</SectionTitle>
      <div className={styles.chartGrid}>
        <ChartCard title="Library by Status">
          {statusData.length > 0 ? (
            <ResponsiveContainer width="100%" height={220}>
              <BarChart
                data={statusData}
                layout="vertical"
                margin={{ top: 4, right: 16, left: 0, bottom: 0 }}
              >
                <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" horizontal={false} />
                <XAxis type="number" allowDecimals={false} tick={TICK} axisLine={false} tickLine={false} />
                <YAxis type="category" dataKey="status" tick={TICK} axisLine={false} tickLine={false} width={88} />
                <Tooltip contentStyle={TOOLTIP_STYLE} />
                <Bar dataKey="count" name="Items" radius={[0, 4, 4, 0]}>
                  {statusData.map((_, i) => (
                    <Cell key={i} fill={PALETTE[i % PALETTE.length]} />
                  ))}
                </Bar>
              </BarChart>
            </ResponsiveContainer>
          ) : (
            <p className={styles.noData}>No library entries yet.</p>
          )}
        </ChartCard>

        <ChartCard title="Status Distribution">
          {statusData.length > 0 ? (
            <ResponsiveContainer width="100%" height={220}>
              <PieChart>
                <Pie
                  data={statusData}
                  dataKey="count"
                  nameKey="status"
                  cx="50%"
                  cy="50%"
                  outerRadius={80}
                  label={({ name, percent }: { name?: string; percent?: number }) =>
                    `${name ?? ''} ${((percent ?? 0) * 100).toFixed(0)}%`
                  }
                  labelLine={false}
                >
                  {statusData.map((_, i) => (
                    <Cell key={i} fill={PALETTE[i % PALETTE.length]} />
                  ))}
                </Pie>
                <Tooltip contentStyle={TOOLTIP_STYLE} />
                <Legend
                  wrapperStyle={{ fontSize: 12, color: 'var(--text-secondary)' }}
                />
              </PieChart>
            </ResponsiveContainer>
          ) : (
            <p className={styles.noData}>No library entries yet.</p>
          )}
        </ChartCard>
      </div>

      {/* ── Database stats ── */}
      {(libStats || diag) && (
        <>
          <SectionTitle>Database</SectionTitle>
          <div className={styles.tableWrap}>
            <table className={styles.table}>
              <tbody>
                {diag && (
                  <>
                    <tr>
                      <td><strong>File</strong></td>
                      <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{diag.dbPath}</td>
                    </tr>
                    <tr>
                      <td><strong>Size</strong></td>
                      <td>{diag.dbExists ? `${(diag.dbSizeBytes / 1024 / 1024).toFixed(2)} MB` : 'File not found'}</td>
                    </tr>
                  </>
                )}
                {libStats && (
                  <tr>
                    <td><strong>Total items</strong></td>
                    <td>{libStats.totalItems.toLocaleString()}</td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
          {libStats && libStats.byMediaType.length > 0 && (
            <div className={styles.tableWrap} style={{ marginTop: 8 }}>
              <table className={styles.table}>
                <thead>
                  <tr><th>Media Type</th><th>Items</th></tr>
                </thead>
                <tbody>
                  {libStats.byMediaType.map(r => (
                    <tr key={r.mediaType}>
                      <td>{r.mediaType}</td>
                      <td>{r.count.toLocaleString()}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}

      {/* ── Recent history table ── */}
      <SectionTitle>Recent Scrobbles</SectionTitle>
      <div className={styles.tableWrap}>
        {history.length === 0 ? (
          <p className={styles.noData}>No scrobbles recorded yet.</p>
        ) : (
          <table className={styles.table}>
            <thead>
              <tr>
                <th>Title</th>
                <th>Progress</th>
                <th>Device</th>
                <th>When</th>
              </tr>
            </thead>
            <tbody>
              {history.slice(0, 20).map(h => (
                <tr key={h.id}>
                  <td>{h.mediaItemName}</td>
                  <td>{h.progressPercent != null ? `${Math.round(h.progressPercent)}%` : '—'}</td>
                  <td>{h.deviceName ?? '—'}</td>
                  <td>{new Date(h.timestamp).toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}
