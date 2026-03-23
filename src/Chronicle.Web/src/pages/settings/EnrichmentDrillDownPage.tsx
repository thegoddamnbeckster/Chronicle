import { useState, useEffect, useCallback, useRef } from 'react'
import { Link, useParams, useSearchParams, useNavigate } from 'react-router-dom'
import {
  getEnrichmentItems,
  getEnrichmentStats,
  resetEnrichmentItem,
  skipEnrichmentItem,
  resetEnrichment,
  type EnrichmentItem,
  type EnrichmentStats,
} from '@/api/enrichment'
import { refreshMediaForPlugin } from '@/api/media'
import styles from './EnrichmentDrillDownPage.module.css'

// ── Constants ────────────────────────────────────────────────────────────────

const STATUS_LABELS: Record<string, string> = {
  All: 'All',
  Pending: 'Pending',
  Completed: 'Completed',
  Failed: 'Failed',
  Exhausted: 'Exhausted',
  NotFound: 'Not Found',
  Skipped: 'Skipped',
}

// ── Helpers ──────────────────────────────────────────────────────────────────

function scoreClass(total: number): string {
  if (total >= 80) return styles.scoreHigh
  if (total >= 50) return styles.scoreMedium
  return styles.scoreLow
}

function fmtDate(iso: string | null): string {
  if (!iso) return 'Never'
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
}

// ── Item card ─────────────────────────────────────────────────────────────────

interface ItemCardProps {
  item: EnrichmentItem
  pluginId: string
  onChanged: () => void
}

function ItemCard({ item, pluginId, onChanged }: ItemCardProps) {
  const [resetting, setResetting] = useState(false)
  const [skipping, setSkipping]   = useState(false)
  const [fixing, setFixing]       = useState(false)
  const [fixInput, setFixInput]   = useState('')
  const [fixOpen, setFixOpen]     = useState(false)
  const navigate = useNavigate()

  const diag    = item.diagnostics
  const scanner = item.fileScannerMetadata

  async function handleReset() {
    setResetting(true)
    try { await resetEnrichmentItem(pluginId, item.mediaItemId); onChanged() }
    finally { setResetting(false) }
  }

  async function handleSkip() {
    setSkipping(true)
    try { await skipEnrichmentItem(pluginId, item.mediaItemId); onChanged() }
    finally { setSkipping(false) }
  }

  async function handleFixMatch() {
    if (!fixInput.trim()) return
    setFixing(true)
    try {
      await refreshMediaForPlugin(item.mediaItemId, pluginId, fixInput.trim())
      setFixOpen(false)
      setFixInput('')
      onChanged()
    } finally { setFixing(false) }
  }

  return (
    <div className={styles.card}>
      {item.posterUrl
        ? <img src={item.posterUrl} alt={item.name} className={styles.poster} />
        : <div className={styles.posterPlaceholder}>🎬</div>
      }

      <div className={styles.body}>
        {/* Title + meta */}
        <div>
          <h3 className={styles.itemName}>
            {item.name}{item.year ? ` (${item.year})` : ''}
          </h3>
          <p className={styles.itemMeta}>
            {item.mediaType}
            {item.hierarchyLevel > 0 ? ` · Level ${item.hierarchyLevel}` : ''}
            {item.externalId ? ` · ${item.externalId}` : ''}
            {' · '}Status: <strong>{STATUS_LABELS[item.status] ?? item.status}</strong>
            {' · '}Last attempt: {fmtDate(item.lastAttemptedAt)}
            {item.retryCount > 0 ? ` · Retries: ${item.retryCount}/${item.maxRetries}` : ''}
          </p>
        </div>

        {/* Scanner signals */}
        {(diag?.scannerSignals || scanner) && (
          <div className={styles.diagSection}>
            <p className={styles.diagTitle}>Scanner Signals</p>
            <div className={styles.signalGrid}>
              {scanner?.folderPath != null && (
                <span className={styles.signal}>
                  <span className={styles.signalYes}>✓</span>
                  Folder: <em>{String(scanner.folderPath)}</em>
                </span>
              )}
              {diag?.scannerSignals != null && (
                <>
                  <span className={styles.signal}>
                    <span className={diag.scannerSignals.hasNfo ? styles.signalYes : styles.signalNo}>
                      {diag.scannerSignals.hasNfo ? '✓' : '✗'}
                    </span>
                    NFO sidecar
                  </span>
                  <span className={styles.signal}>
                    <span className={diag.scannerSignals.hasLocalPoster ? styles.signalYes : styles.signalNo}>
                      {diag.scannerSignals.hasLocalPoster ? '✓' : '✗'}
                    </span>
                    Local poster
                  </span>
                </>
              )}
              {Array.isArray(scanner?.filePaths) && (
                <span className={styles.signal}>
                  <span className={styles.signalYes}>✓</span>
                  {(scanner!.filePaths as string[]).length} file(s)
                </span>
              )}
            </div>
          </div>
        )}

        {/* Enrichment diagnostics */}
        {diag && (
          <div className={styles.diagSection}>
            <p className={styles.diagTitle}>Enrichment Diagnostics</p>
            {diag.searchQuery && (
              <p className={styles.searchLine}>
                Searched: <code className={styles.searchQuery}>{diag.searchQuery}</code>
                {' — '}{diag.candidatesReturned} candidate(s) returned
              </p>
            )}
            {diag.failureReason && item.status !== 'Completed' && (
              <p className={styles.failureReason}>{diag.failureReason}</p>
            )}
            {diag.topCandidates.length > 0 && (
              <>
                <p className={styles.diagTitle} style={{ marginTop: 10 }}>Top Candidates</p>
                <div className={styles.candidates}>
                  {diag.topCandidates.map((c, i) => (
                    <div key={i} className={styles.candidate}>
                      <span className={styles.candidateName}>
                        {c.title ?? '(no title)'}{c.year ? ` (${c.year})` : ''}
                      </span>
                      {c.externalId && (
                        <code style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>
                          {c.externalId}
                        </code>
                      )}
                      <div className={styles.scoreBar}>
                        <span>title {c.titleScore}pt</span>
                        <span>year {c.yearScore}pt</span>
                        <span className={`${styles.scorePill} ${scoreClass(c.totalScore)}`}>
                          {c.totalScore}/100
                        </span>
                      </div>
                    </div>
                  ))}
                </div>
              </>
            )}
          </div>
        )}

        {/* Error message */}
        {item.errorMessage && (
          <div className={styles.diagSection}>
            <p className={styles.diagTitle}>Error</p>
            <div className={styles.errorMsg}>{item.errorMessage}</div>
          </div>
        )}

        {/* Fix Match inline panel */}
        {fixOpen && (
          <div className={styles.diagSection}>
            <p className={styles.diagTitle}>Fix Match — enter an ID or URL</p>
            <div className={styles.fixMatchRow}>
              <input
                type="text"
                autoFocus
                className={styles.fixMatchInput}
                placeholder="e.g. movie:550 or https://..."
                value={fixInput}
                onChange={e => setFixInput(e.target.value)}
                onKeyDown={e => {
                  if (e.key === 'Enter' && fixInput.trim()) handleFixMatch()
                  if (e.key === 'Escape') { setFixOpen(false); setFixInput('') }
                }}
              />
              <button
                className={styles.actionBtnPrimary}
                onClick={handleFixMatch}
                disabled={fixing || !fixInput.trim()}
              >
                {fixing ? 'Applying…' : 'Apply'}
              </button>
            </div>
          </div>
        )}

        {/* Actions */}
        <div className={styles.actions}>
          {item.hierarchyLevel === 0 && item.status !== 'Skipped' && (
            <button
              className={styles.actionBtnPrimary}
              onClick={() => setFixOpen(v => !v)}
            >
              ✎ Fix Match
            </button>
          )}
          {item.status !== 'Skipped' && (
            <button className={styles.actionBtn} onClick={handleSkip} disabled={skipping}>
              {skipping ? 'Skipping…' : '⊘ Skip'}
            </button>
          )}
          {item.status !== 'Completed' && item.status !== 'Pending' && (
            <button className={styles.actionBtn} onClick={handleReset} disabled={resetting}>
              {resetting ? 'Resetting…' : '↺ Reset & Retry'}
            </button>
          )}
          <button
            className={styles.actionBtn}
            onClick={() => navigate(`/media/${item.mediaItemId}`)}
          >
            View in Library →
          </button>
        </div>
      </div>
    </div>
  )
}

// ── Main page ─────────────────────────────────────────────────────────────────

export default function EnrichmentDrillDownPage() {
  const { pluginId = '' }               = useParams<{ pluginId: string }>()
  const [searchParams, setSearchParams] = useSearchParams()

  const activeStatus = searchParams.get('status') ?? 'All'
  const [search, setSearch]                   = useState('')
  const [debouncedSearch, setDebouncedSearch] = useState('')
  const [page, setPage]                       = useState(1)
  const [loading, setLoading]                 = useState(true)
  const [items, setItems]                     = useState<EnrichmentItem[]>([])
  const [total, setTotal]                     = useState(0)
  const [totalPages, setTotalPages]           = useState(1)
  const [stats, setStats]                     = useState<EnrichmentStats | null>(null)
  const [bulkWorking, setBulkWorking]         = useState(false)
  const debounceRef = useRef<ReturnType<typeof setTimeout>>()

  useEffect(() => {
    clearTimeout(debounceRef.current)
    debounceRef.current = setTimeout(() => {
      setDebouncedSearch(search)
      setPage(1)
    }, 300)
    return () => clearTimeout(debounceRef.current)
  }, [search])

  const load = useCallback(async () => {
    setLoading(true)
    try {
      const statusParam = activeStatus === 'All' ? undefined : activeStatus
      const res = await getEnrichmentItems(pluginId, statusParam, page, 25, debouncedSearch || undefined)
      setItems(res.items)
      setTotal(res.total)
      setTotalPages(res.totalPages)
    } finally {
      setLoading(false)
    }
  }, [pluginId, activeStatus, page, debouncedSearch])

  useEffect(() => {
    getEnrichmentStats()
      .then(all => setStats(all.find(s => s.pluginId === pluginId) ?? null))
      .catch(() => {})
  }, [pluginId])

  useEffect(() => { load() }, [load])

  function setStatus(s: string) {
    setSearchParams(s === 'All' ? {} : { status: s })
    setPage(1)
  }

  async function handleBulkReset() {
    setBulkWorking(true)
    try {
      const scope = activeStatus === 'Exhausted' ? 'exhausted' : 'all'
      await resetEnrichment(pluginId, scope)
      await load()
    } finally { setBulkWorking(false) }
  }

  const statusCounts = stats
    ? {
        All:       stats.pending + stats.completed + stats.failed + stats.exhausted + stats.notFound + stats.skipped,
        Pending:   stats.pending,
        Completed: stats.completed,
        Failed:    stats.failed,
        Exhausted: stats.exhausted,
        NotFound:  stats.notFound,
        Skipped:   stats.skipped,
      }
    : ({} as Record<string, number>)

  const pluginDisplayName = stats?.pluginName ?? pluginId

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <Link to="/settings/background-tasks" className={styles.backLink}>
          ← Background Tasks
        </Link>
        <h1 className={styles.title}>Enrichment — {pluginDisplayName}</h1>
      </div>

      {/* Status tabs */}
      <div className={styles.tabs}>
        {Object.entries(STATUS_LABELS).map(([key, label]) => (
          <button
            key={key}
            className={`${styles.tab} ${activeStatus === key ? styles.tabActive : ''}`}
            onClick={() => setStatus(key)}
          >
            {label}{statusCounts[key] != null ? ` (${statusCounts[key]})` : ''}
          </button>
        ))}
      </div>

      {/* Toolbar */}
      <div className={styles.toolbar}>
        <input
          type="text"
          className={styles.searchInput}
          placeholder="Search by name…"
          value={search}
          onChange={e => setSearch(e.target.value)}
        />
        <span className={styles.totalCount}>{total} item(s)</span>
        {activeStatus !== 'All' && activeStatus !== 'Completed' && activeStatus !== 'Skipped' && (
          <button
            className={styles.bulkBtn}
            onClick={handleBulkReset}
            disabled={bulkWorking || total === 0}
          >
            {bulkWorking ? 'Resetting…' : `↺ Reset All ${STATUS_LABELS[activeStatus] ?? activeStatus}`}
          </button>
        )}
      </div>

      {/* Cards */}
      {loading ? (
        <p className={styles.loading}>Loading…</p>
      ) : items.length === 0 ? (
        <p className={styles.empty}>No items in this category.</p>
      ) : (
        <div className={styles.cards}>
          {items.map(item => (
            <ItemCard
              key={item.enrichmentId}
              item={item}
              pluginId={pluginId}
              onChanged={load}
            />
          ))}
        </div>
      )}

      {/* Pagination */}
      {totalPages > 1 && (
        <div className={styles.pagination}>
          <button className={styles.pageBtn} disabled={page <= 1} onClick={() => setPage(p => p - 1)}>
            ← Prev
          </button>
          <span className={styles.pageInfo}>Page {page} of {totalPages}</span>
          <button className={styles.pageBtn} disabled={page >= totalPages} onClick={() => setPage(p => p + 1)}>
            Next →
          </button>
        </div>
      )}
    </div>
  )
}
