import { useState, useEffect, useCallback } from 'react'
import AdvancedToggle from '@/components/ui/AdvancedToggle'
import {
  getBackgroundTasks,
  updateBackgroundTask,
  runBackgroundTask,
  type BackgroundTask,
} from '@/api/backgroundTasks'
import {
  getEnrichmentStats,
  runEnrichment,
  resetEnrichment,
  type EnrichmentStats,
} from '@/api/enrichment'
import { getImportProgress, type ImportProgressState } from '@/api/scan'
import {
  cronToParams,
  paramsToCron,
  describeSchedule,
  validateParams,
  DEFAULT_PARAMS,
  type Frequency,
  type ScheduleParams,
} from '@/utils/cronBuilder'
import { ApiError } from '@/api/client'
import styles from './BackgroundTasksPage.module.css'

// ── Helpers ─────────────────────────────────────────────────────────────────

/** Format a UTC ISO string as a local datetime string. */
function fmtLocal(iso: string | null): string {
  if (!iso) return 'Never'
  return new Date(iso).toLocaleString(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  })
}

/** Format a UTC ISO string as a relative string (e.g. "2 hours ago"). */
function fmtRelative(iso: string | null): string {
  if (!iso) return 'Never'
  const diffMs = Date.now() - new Date(iso).getTime()
  const diffSec = Math.round(diffMs / 1000)
  if (Math.abs(diffSec) < 60) return 'Just now'
  const diffMin = Math.round(diffSec / 60)
  if (Math.abs(diffMin) < 60) return `${Math.abs(diffMin)}m ${diffMs > 0 ? 'ago' : 'from now'}`
  const diffHr = Math.round(diffMin / 60)
  if (Math.abs(diffHr) < 24) return `${Math.abs(diffHr)}h ${diffMs > 0 ? 'ago' : 'from now'}`
  const diffDay = Math.round(diffHr / 24)
  return `${Math.abs(diffDay)}d ${diffMs > 0 ? 'ago' : 'from now'}`
}

function statusBadge(task: BackgroundTask) {
  if (task.isRunning) return { cls: styles.running, label: 'Running' }
  if (task.lastRunSucceeded === null) return { cls: styles.idle, label: 'Idle' }
  if (task.lastRunSucceeded) return { cls: styles.success, label: 'Success' }
  return { cls: styles.failed, label: 'Failed' }
}

const DOW_LABELS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']


// ── Scan import progress banner ───────────────────────────────────────────
// Shown while the scheduled scan is actively importing groups. Polls
// GET /scan/import-progress every second so the user can see live updates
// without needing to navigate to the Scan page.

interface ScanProgressBannerProps {
  /** Whether the scheduled_scan task is currently marked as running. */
  scanIsRunning: boolean
}

function ScanProgressBanner({ scanIsRunning }: ScanProgressBannerProps) {
  const [progress, setProgress] = useState<ImportProgressState | null>(null)

  useEffect(() => {
    if (!scanIsRunning) {
      setProgress(null)
      return
    }

    // Fetch immediately, then every second while running
    let cancelled = false
    async function poll() {
      try {
        const p = await getImportProgress()
        if (!cancelled) setProgress(p)
      } catch {
        // silently ignore transient errors
      }
    }

    poll()
    const id = setInterval(poll, 1000)
    return () => {
      cancelled = true
      clearInterval(id)
    }
  }, [scanIsRunning])

  if (!scanIsRunning || !progress?.isRunning) return null

  const pct = progress.total > 0 ? Math.round((progress.processed / progress.total) * 100) : 0

  return (
    <div className={styles.scanProgressBanner}>
      <div className={styles.scanProgressHeader}>
        <span className={styles.progressSpinnerInline} />
        <span className={styles.scanProgressTitle}>
          Importing: {progress.processed} of {progress.total} groups
          {progress.currentItemName && (
            <span className={styles.scanProgressCurrent}> — {progress.currentItemName}</span>
          )}
        </span>
        <span className={styles.scanProgressPct}>{pct}%</span>
      </div>
      <div className={styles.scanProgressTrack}>
        <div className={styles.scanProgressFill} style={{ width: `${pct}%` }} />
      </div>
    </div>
  )
}

// ── Enrichment status section ─────────────────────────────────────────────

interface EnrichmentSectionProps {
  /** True when any enrichment background task is actively running. */
  enrichmentRunning: boolean
}

function EnrichmentSection({ enrichmentRunning }: EnrichmentSectionProps) {
  const [stats, setStats] = useState<EnrichmentStats[]>([])
  const [loading, setLoading] = useState(true)
  const [refreshing, setRefreshing] = useState(false)
  const [runStarted, setRunStarted] = useState<Record<string, boolean>>({})

  const load = useCallback(async () => {
    try {
      const data = await getEnrichmentStats()
      setStats(data)
    } catch {
      // silently ignore — section will show empty
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { load() }, [load])

  // Poll at 2s while enrichment is actively running so numbers update in near
  // real-time; fall back to 10s when idle to keep API traffic low.
  useEffect(() => {
    const interval = enrichmentRunning ? 2_000 : 10_000
    const id = setInterval(load, interval)
    return () => clearInterval(id)
  }, [load, enrichmentRunning])

  async function handleRefresh() {
    setRefreshing(true)
    await load()
    setRefreshing(false)
  }

  async function handleRun(pluginId: string) {
    try {
      await runEnrichment(pluginId)
      setRunStarted(prev => ({ ...prev, [pluginId]: true }))
      setTimeout(() => setRunStarted(prev => ({ ...prev, [pluginId]: false })), 3000)
    } catch {
      // ignore
    }
  }

  async function handleReset(pluginId: string, scope: 'exhausted' | 'all') {
    try {
      await resetEnrichment(pluginId, scope)
      await load()
    } catch {
      // ignore
    }
  }

  return (
    <div className={styles.enrichmentSection}>
      <div className={styles.sectionHeader}>
        <h2 className={styles.sectionTitle}>Enrichment Status</h2>
        <button
          className={styles.refreshBtn}
          onClick={handleRefresh}
          disabled={refreshing}
          title="Refresh enrichment stats"
        >
          {refreshing ? '↻ Refreshing…' : '↻ Refresh'}
        </button>
      </div>
      {loading ? (
        <p className={styles.loading}>Loading enrichment stats…</p>
      ) : stats.length === 0 ? (
        <p className={styles.enrichmentEmpty}>No metadata plugins installed. Install a plugin in Settings → Plugins to enable enrichment.</p>
      ) : (
        <div className={styles.card}>
          <table className={styles.enrichTable}>
            <thead>
              <tr>
                <th className={styles.enrichTh}>Plugin</th>
                <th className={styles.enrichTh}>Pending</th>
                <th className={styles.enrichTh}>Completed</th>
                <th className={styles.enrichTh}>Failed</th>
                <th className={styles.enrichTh}>Exhausted</th>
                <th className={styles.enrichTh}>Not Found</th>
                <th className={styles.enrichTh}>Skipped</th>
                <th className={styles.enrichTh}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {stats.map(s => (
                <tr key={s.pluginId} className={styles.enrichRow}>
                  <td className={styles.enrichTd}>{s.pluginName}</td>
                  <td className={styles.enrichTd}>{s.pending}</td>
                  <td className={styles.enrichTd}>{s.completed}</td>
                  <td className={styles.enrichTd}>{s.failed}</td>
                  <td className={styles.enrichTd}>{s.exhausted}</td>
                  <td className={styles.enrichTd}>{s.notFound}</td>
                  <td className={styles.enrichTd}>{s.skipped}</td>
                  <td className={`${styles.enrichTd} ${styles.enrichActions}`}>
                    <button
                      className={styles.runBtn}
                      onClick={() => handleRun(s.pluginId)}
                    >
                      {runStarted[s.pluginId] ? 'Started' : 'Run Now'}
                    </button>
                    <button
                      className={styles.editBtn}
                      onClick={() => handleReset(s.pluginId, 'exhausted')}
                      title="Reset items marked as exhausted so they will be retried"
                    >
                      Reset Exhausted
                    </button>
                    <button
                      className={styles.editBtn}
                      onClick={() => handleReset(s.pluginId, 'all')}
                      title="Reset all enrichment state for this plugin"
                    >
                      Reset All
                    </button>
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

// ── Schedule editor ──────────────────────────────────────────────────────────

interface ScheduleEditorProps {
  taskId: string
  initialCron: string
  isEnabled: boolean
  onSave: (taskId: string, cron: string, enabled: boolean) => Promise<void>
  onCancel: () => void
}

function ScheduleEditor({ taskId, initialCron, isEnabled, onSave, onCancel }: ScheduleEditorProps) {
  const [params, setParams] = useState<ScheduleParams>(
    () => cronToParams(initialCron) ?? DEFAULT_PARAMS,
  )
  const [rawCron, setRawCron] = useState(initialCron)
  const [useRaw, setUseRaw]   = useState(cronToParams(initialCron) === null)
  const [enabled, setEnabled] = useState(isEnabled)
  const [saving, setSaving]   = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  function updateParams(next: Partial<ScheduleParams>) {
    const merged = { ...params, ...next }
    setParams(merged)
    setRawCron(paramsToCron(merged))
  }

  function handleRawChange(val: string) {
    setRawCron(val)
    const parsed = cronToParams(val)
    if (parsed) setParams(parsed)
  }

  function validateRaw(): { ok: boolean; msg: string } {
    const parts = rawCron.trim().split(/\s+/)
    if (parts.length !== 5) {
      return {
        ok: false,
        msg: "This isn't a valid cron expression. A cron expression has five fields: minute, hour, day-of-month, month, day-of-week. Example: 0 */4 * * * (every 4 hours).",
      }
    }
    return { ok: true, msg: '' }
  }

  const paramError = validateParams(params)
  const rawValidation = validateRaw()
  const canSave = useRaw ? rawValidation.ok : paramError === null

  async function handleSave() {
    if (!canSave) return
    setSaving(true)
    setSaveError(null)
    try {
      await onSave(taskId, useRaw ? rawCron.trim() : paramsToCron(params), enabled)
    } catch (err) {
      if (err instanceof ApiError) setSaveError(err.message)
      else setSaveError('An unexpected error occurred. Please try again.')
    } finally {
      setSaving(false)
    }
  }

  const freq = params.frequency

  return (
    <div className={styles.scheduleEditor}>
      <h3 className={styles.editorTitle}>Edit Schedule</h3>

      {/* Enable toggle */}
      <div className={styles.formRow}>
        <span className={styles.label}>Enabled</span>
        <button
          role="switch"
          aria-checked={enabled}
          className={`${styles.toggle} ${enabled ? styles.toggleOn : ''}`}
          onClick={() => setEnabled(!enabled)}
        >
          <span className={styles.toggleThumb} />
        </button>
      </div>

      {/* Frequency */}
      <div className={styles.formRow}>
        <span className={styles.label}>Frequency</span>
        <select
          className={styles.select}
          value={freq}
          onChange={e => updateParams({ frequency: e.target.value as Frequency })}
        >
          <option value="minutes">Minutes</option>
          <option value="hours">Hours</option>
          <option value="daily">Daily</option>
          <option value="weekly">Weekly</option>
          <option value="monthly">Monthly</option>
        </select>
      </div>

      {/* Interval (minutes / hours) */}
      {(freq === 'minutes' || freq === 'hours') && (
        <div className={styles.formRow}>
          <span className={styles.label}>Every</span>
          <input
            type="number"
            className={styles.numberInput}
            min={1}
            max={freq === 'minutes' ? 59 : 23}
            value={params.interval}
            onChange={e => updateParams({ interval: parseInt(e.target.value) || 1 })}
          />
          <span className={styles.label}>{freq}</span>
        </div>
      )}

      {/* Time of day */}
      {(freq === 'daily' || freq === 'weekly' || freq === 'monthly') && (
        <div className={styles.formRow}>
          <span className={styles.label}>At</span>
          <input
            type="time"
            className={styles.timeInput}
            value={`${String(params.timeHour).padStart(2, '0')}:${String(params.timeMinute).padStart(2, '0')}`}
            onChange={e => {
              const [h, m] = e.target.value.split(':').map(Number)
              updateParams({ timeHour: h, timeMinute: m })
            }}
          />
        </div>
      )}

      {/* Day of week */}
      {freq === 'weekly' && (
        <div className={styles.dowRow}>
          {DOW_LABELS.map((label, i) => (
            <button
              key={i}
              className={`${styles.dowBtn} ${params.daysOfWeek.includes(i) ? styles.dowBtnActive : ''}`}
              onClick={() => {
                const next = params.daysOfWeek.includes(i)
                  ? params.daysOfWeek.filter(d => d !== i)
                  : [...params.daysOfWeek, i].sort()
                updateParams({ daysOfWeek: next })
              }}
            >
              {label}
            </button>
          ))}
        </div>
      )}

      {/* Day of month */}
      {freq === 'monthly' && (
        <div className={styles.formRow}>
          <span className={styles.label}>On day</span>
          <input
            type="number"
            className={styles.numberInput}
            min={1}
            max={31}
            value={params.dayOfMonth}
            onChange={e => updateParams({ dayOfMonth: parseInt(e.target.value) || 1 })}
          />
          <span className={styles.label}>of the month</span>
        </div>
      )}

      {!useRaw && paramError && <p className={styles.fieldError}>{paramError}</p>}
      {!useRaw && !paramError && <p className={styles.preview}>{describeSchedule(params)}</p>}

      {/* Advanced: raw cron */}
      <AdvancedToggle label="Advanced: edit cron expression directly">
        <div className={styles.formRow}>
          <input
            type="text"
            className={styles.cronInput}
            value={rawCron}
            onChange={e => { setUseRaw(true); handleRawChange(e.target.value) }}
            onFocus={() => setUseRaw(true)}
            placeholder="0 */4 * * *"
            spellCheck={false}
          />
        </div>
        {useRaw && (
          <p className={`${styles.cronPreview} ${rawValidation.ok ? styles.cronOk : styles.cronErr}`}>
            {rawValidation.ok ? 'Cron expression looks valid.' : rawValidation.msg}
          </p>
        )}
      </AdvancedToggle>

      {saveError && <p className={styles.saveError}>{saveError}</p>}

      <div className={styles.editorButtons}>
        <button className={styles.saveBtn} onClick={handleSave} disabled={saving || !canSave}>
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button className={styles.cancelBtn} onClick={onCancel}>Cancel</button>
      </div>
    </div>
  )
}

// ── Main page ────────────────────────────────────────────────────────────────

export default function BackgroundTasksPage() {
  const [tasks, setTasks]     = useState<BackgroundTask[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError]     = useState<string | null>(null)
  const [editingId, setEditingId]   = useState<string | null>(null)
  const [runningIds, setRunningIds] = useState<Set<string>>(new Set())

  const load = useCallback(async () => {
    try {
      const data = await getBackgroundTasks()
      setTasks(data)
      setError(null)
    } catch {
      setError('Could not reach the Chronicle API. Check that the service is running.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { load() }, [load])

  // Poll every 3s while any task is running
  useEffect(() => {
    const anyRunning = tasks.some(t => t.isRunning || runningIds.has(t.taskId))
    if (!anyRunning) return
    const id = setInterval(load, 3000)
    return () => clearInterval(id)
  }, [tasks, runningIds, load])

  async function handleRunNow(taskId: string) {
    setRunningIds(prev => new Set(prev).add(taskId))
    try {
      await runBackgroundTask(taskId)
      await load()
    } catch (err) {
      if (err instanceof ApiError) {
        if (err.statusCode === 409) await load()
        else alert(err.message)
      }
    } finally {
      setRunningIds(prev => { const s = new Set(prev); s.delete(taskId); return s })
    }
  }

  async function handleSave(taskId: string, cron: string, isEnabled: boolean) {
    await updateBackgroundTask(taskId, { cronExpression: cron, isEnabled })
    setEditingId(null)
    await load()
  }

  if (loading) return <div className={styles.page}><p className={styles.loading}>Loading background tasks…</p></div>
  if (error)   return <div className={styles.page}><p className={styles.errorMsg}>{error}</p></div>

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Background Tasks</h1>

      {tasks.map(task => {
        const { cls, label } = statusBadge(task)
        const isRunning = task.isRunning || runningIds.has(task.taskId)
        const brandColor = task.brandColorDark ?? undefined

        return (
          <div
            key={task.taskId}
            className={`${styles.card} ${task.pluginId ? styles.cardPlugin : ''}`}
            style={brandColor ? { '--plugin-brand-color': brandColor } as React.CSSProperties : undefined}
          >
            <div className={styles.cardHeader}>
              <div className={styles.cardTitleGroup}>
                {task.pluginIconUrl && (
                  <img
                    src={task.pluginIconUrl}
                    alt={task.pluginName ?? ''}
                    className={styles.pluginIcon}
                  />
                )}
                <div className={styles.cardTitleText}>
                  <h2 className={styles.taskName}>
                    {task.pluginName ? `${task.pluginName} · ${task.displayName}` : task.displayName}
                  </h2>
                  <p className={styles.taskDesc}>{task.description}</p>
                </div>
              </div>
              <div className={styles.cardActions}>
                <span className={`${styles.badge} ${cls}`}>{label}</span>
                <button
                  role="switch"
                  aria-checked={task.isEnabled}
                  className={`${styles.toggle} ${task.isEnabled ? styles.toggleOn : ''}`}
                  onClick={() =>
                    updateBackgroundTask(task.taskId, { isEnabled: !task.isEnabled }).then(load)
                  }
                  title={task.isEnabled ? 'Disable task' : 'Enable task'}
                >
                  <span className={styles.toggleThumb} />
                </button>
                <button
                  className={styles.runBtn}
                  onClick={() => handleRunNow(task.taskId)}
                  disabled={isRunning}
                >
                  {isRunning ? 'Running…' : 'Run Now'}
                </button>
                <button
                  className={styles.editBtn}
                  onClick={() => setEditingId(editingId === task.taskId ? null : task.taskId)}
                >
                  {editingId === task.taskId ? 'Close' : 'Schedule'}
                </button>
              </div>
            </div>

            {task.lastRunSucceeded === false && task.lastErrorMessage && !isRunning && (
              <p className={styles.errorText}>{task.lastErrorMessage}</p>
            )}

            <div className={styles.metaGrid}>
              <div className={styles.metaRow}>
                <span className={styles.metaLabel}>Last Run</span>
                <span
                  className={styles.metaValue}
                  title={task.lastRunAt ? fmtLocal(task.lastRunAt) : undefined}
                >
                  {fmtRelative(task.lastRunAt)}
                </span>
              </div>
              <div className={styles.metaRow}>
                <span className={styles.metaLabel}>Next Run</span>
                <span
                  className={styles.metaValue}
                  title={task.nextRunAt ? fmtLocal(task.nextRunAt) : undefined}
                >
                  {task.isEnabled ? fmtRelative(task.nextRunAt) : 'Disabled'}
                </span>
              </div>
              <div className={styles.metaRow}>
                <span className={styles.metaLabel}>Schedule</span>
                <span className={styles.metaValue} style={{ fontFamily: 'monospace', fontSize: '0.82rem' }}>
                  {task.cronExpression}
                </span>
              </div>
            </div>

            {editingId === task.taskId && (
              <ScheduleEditor
                taskId={task.taskId}
                initialCron={task.cronExpression}
                isEnabled={task.isEnabled}
                onSave={handleSave}
                onCancel={() => setEditingId(null)}
              />
            )}
          </div>
        )
      })}

      <ScanProgressBanner
        scanIsRunning={tasks.some(t => t.taskId === 'scheduled_scan' && (t.isRunning || runningIds.has(t.taskId)))}
      />

      <EnrichmentSection
        enrichmentRunning={tasks.some(t =>
          t.taskId.endsWith('fetch-missing-metadata') &&
          (t.isRunning || runningIds.has(t.taskId))
        )}
      />
    </div>
  )
}
