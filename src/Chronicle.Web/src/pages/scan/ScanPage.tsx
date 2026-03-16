import { useState, useEffect, useRef } from 'react'
import { useQuery, useMutation } from '@tanstack/react-query'
import { getScanStatus, getScanProgress, previewGrouped, importGroups } from '@/api/scan'
import type { ScanProgress } from '@/api/scan'
import { getMediaTypes } from '@/api/media'
import { useBackgroundActivity } from '@/contexts/BackgroundActivityContext'
import type { ScanGroupResult, MediaTypeOption } from '@/types'
import PathInput from '@/components/PathInput'
import ScanGroupCard, { groupToPayload } from './ScanGroupCard'
import styles from './ScanPage.module.css'

type Step = 'configure' | 'preview' | 'review' | 'done'

export default function ScanPage() {
  // ── Configuration state ──────────────────────────────────────────────────
  const [path, setPath] = useState('')
  const [recursive, setRecursive] = useState(true)
  const [mediaTypeId, setMediaTypeId] = useState<number | ''>('')

  // ── Pipeline state ───────────────────────────────────────────────────────
  const [step, setStep] = useState<Step>('configure')
  const [groupResult, setGroupResult] = useState<ScanGroupResult | null>(null)
  const [rejectedKeys, setRejectedKeys] = useState<Set<string>>(new Set())
  const [importResult, setImportResult] = useState<{ imported: number; failed: number; duplicates: number } | null>(null)
  const [error, setError] = useState<string | null>(null)

  // ── Scan progress (polled while preview mutation is pending) ─────────────
  const [scanProgress, setScanProgress] = useState<ScanProgress | null>(null)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const { addJob, completeJob, failJob } = useBackgroundActivity()

  // ── Queries ──────────────────────────────────────────────────────────────
  const { data: status } = useQuery({ queryKey: ['scan-status'], queryFn: getScanStatus })
  const { data: allMediaTypes = [] } = useQuery({ queryKey: ['media-types'], queryFn: getMediaTypes })

  const supportedTypes: MediaTypeOption[] = allMediaTypes.filter((t) =>
    status?.supportedMediaTypeNames.includes(t.name),
  )

  // ── Mutations ─────────────────────────────────────────────────────────────
  const previewMut = useMutation({
    mutationFn: () => {
      if (!mediaTypeId) throw new Error('Select a media type.')
      return previewGrouped({ path: path.trim(), recursive, mediaTypeId: Number(mediaTypeId) })
    },
    onSuccess: (data) => {
      setGroupResult(data)
      setRejectedKeys(new Set())
      setError(null)
      setStep('preview')
    },
    onError: (err: Error) => setError(err.message),
  })

  const importMut = useMutation({
    mutationFn: () => {
      if (!groupResult) throw new Error('No scan result.')
      const toImport = groupResult.groups
        .filter(g => !rejectedKeys.has(g.groupKey))
        .map(groupToPayload)
      if (toImport.length === 0) throw new Error('No groups selected for import.')
      return importGroups({ groups: toImport, mediaTypeId: Number(mediaTypeId) })
    },
    onMutate: () => {
      const count = groupResult?.groups.filter(g => !rejectedKeys.has(g.groupKey)).length ?? 0
      return addJob(`Importing ${count} groups…`)
    },
    onSuccess: (data, _vars, jobId) => {
      setImportResult({ imported: data.imported, failed: data.failed, duplicates: data.duplicates })
      setStep('done')
      completeJob(jobId as string, `${data.imported} imported`)
    },
    onError: (err: Error, _vars, jobId) => {
      setError(err.message)
      failJob(jobId as string, err.message)
    },
  })

  // ── Scan progress polling ────────────────────────────────────────────────
  useEffect(() => {
    if (previewMut.isPending) {
      setScanProgress(null)
      pollRef.current = setInterval(async () => {
        try {
          const p = await getScanProgress()
          setScanProgress(p)
        } catch {
          // ignore polling errors
        }
      }, 500)
    } else {
      if (pollRef.current) {
        clearInterval(pollRef.current)
        pollRef.current = null
      }
      if (!previewMut.isPending) setScanProgress(null)
    }
    return () => {
      if (pollRef.current) {
        clearInterval(pollRef.current)
        pollRef.current = null
      }
    }
  }, [previewMut.isPending])

  // ── Helpers ───────────────────────────────────────────────────────────────
  const toggleRejected = (key: string) => {
    setRejectedKeys(prev => {
      const next = new Set(prev)
      next.has(key) ? next.delete(key) : next.add(key)
      return next
    })
  }

  const canScan = path.trim() !== '' && mediaTypeId !== '' && !previewMut.isPending

  function reset() {
    setStep('configure')
    setGroupResult(null)
    setRejectedKeys(new Set())
    setImportResult(null)
    setError(null)
  }

  // ── Render ────────────────────────────────────────────────────────────────
  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1 className={styles.title}>File Scan</h1>
        {step !== 'configure' && step !== 'done' && (
          <button className={styles.resetBtn} onClick={reset}>Start over</button>
        )}
      </div>

      {/* Step indicator */}
      <div className={styles.stepBar}>
        {(['configure', 'preview', 'review', 'done'] as const).map((s, i) => (
          <div key={s} className={`${styles.stepItem} ${step === s ? styles.stepActive : ''} ${isStepDone(step, s) ? styles.stepDone : ''}`}>
            <span className={styles.stepNum}>{i + 1}</span>
            <span className={styles.stepLabel}>{stepLabel(s)}</span>
          </div>
        ))}
      </div>

      {error && <p className={styles.errorMsg}>{error}</p>}

      {/* ── Step 1: Configure ────────────────────────────────────────────── */}
      {step === 'configure' && (
        <div className={styles.formCard}>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="scan-path">Directory path</label>
            <PathInput
              id="scan-path"
              className={styles.textInput}
              placeholder="C:\Movies or /mnt/media/movies"
              value={path}
              onChange={setPath}
            />
          </div>

          <div className={styles.row}>
            <div className={styles.field}>
              <label className={styles.label} htmlFor="media-type">Media type</label>
              <select
                id="media-type"
                className={styles.select}
                value={mediaTypeId}
                onChange={(e) => setMediaTypeId(e.target.value === '' ? '' : Number(e.target.value))}
              >
                <option value="">— select type —</option>
                {supportedTypes.map((t) => (
                  <option key={t.id} value={t.id}>{t.displayName}</option>
                ))}
              </select>
            </div>
          </div>

          <div className={styles.checkRow}>
            <label className={styles.checkLabel}>
              <input type="checkbox" checked={recursive} onChange={(e) => setRecursive(e.target.checked)} />
              Include subdirectories
            </label>
          </div>

          <button
            className={styles.scanBtn}
            disabled={!canScan}
            onClick={() => previewMut.mutate()}
          >
            {previewMut.isPending ? 'Scanning…' : 'Scan Directory'}
          </button>

          {/* Real-time per-folder progress shown while the scan runs */}
          {previewMut.isPending && (
            <div className={styles.progressPanel}>
              {scanProgress?.currentFolder ? (
                <>
                  <div className={styles.progressRow}>
                    <span className={styles.progressSpinner} />
                    <span className={styles.progressLabel}>
                      Folder {scanProgress.foldersScanned} of {scanProgress.totalFolders}
                      {scanProgress.filesFound > 0 && ` · ${scanProgress.filesFound} files found`}
                    </span>
                  </div>
                  <div className={styles.progressFolder} title={scanProgress.currentFolder}>
                    {scanProgress.currentFolder}
                  </div>
                </>
              ) : (
                <div className={styles.progressRow}>
                  <span className={styles.progressSpinner} />
                  <span className={styles.progressLabel}>Enumerating directories…</span>
                </div>
              )}
            </div>
          )}
        </div>
      )}

      {/* ── Step 2: Preview ──────────────────────────────────────────────── */}
      {step === 'preview' && groupResult && (
        <div className={styles.resultCard}>
          <div className={styles.resultHeader}>
            <h2 className={styles.resultTitle}>
              Found {groupResult.totalGroups} group{groupResult.totalGroups !== 1 ? 's' : ''}
              <span className={styles.subtitle}> ({groupResult.totalFiles} files)</span>
            </h2>
            <button
              className={styles.scanBtn}
              disabled={groupResult.groups.length === 0}
              onClick={() => setStep('review')}
            >
              Review {groupResult.groups.length} groups →
            </button>
          </div>

          <div className={styles.groupList}>
            {groupResult.groups.map(g => (
              <ScanGroupCard
                key={g.groupKey}
                group={g}
                checked={!rejectedKeys.has(g.groupKey)}
                onToggle={toggleRejected}
              />
            ))}
          </div>

          {groupResult.ungrouped.length > 0 && (
            <details className={styles.ungroupedSection}>
              <summary className={styles.ungroupedSummary}>
                {groupResult.ungrouped.length} ungrouped file{groupResult.ungrouped.length !== 1 ? 's' : ''} (will not be imported)
              </summary>
              <ul className={styles.ungroupedList}>
                {groupResult.ungrouped.map(f => <li key={f} className={styles.ungroupedFile}>{f}</li>)}
              </ul>
            </details>
          )}
        </div>
      )}

      {/* ── Step 3: Review ───────────────────────────────────────────────── */}
      {step === 'review' && groupResult && (
        <div className={styles.resultCard}>
          <div className={styles.resultHeader}>
            <h2 className={styles.resultTitle}>
              {groupResult.groups.length - rejectedKeys.size} of {groupResult.groups.length} groups selected
            </h2>
            <div className={styles.headerActions}>
              <button className={styles.secondaryBtn} onClick={() => setStep('preview')}>
                ← Back to preview
              </button>
              <button
                className={styles.scanBtn}
                disabled={(groupResult.groups.length - rejectedKeys.size) === 0 || importMut.isPending}
                onClick={() => importMut.mutate()}
              >
                {importMut.isPending
                  ? 'Importing…'
                  : `Import ${groupResult.groups.length - rejectedKeys.size} groups →`}
              </button>
            </div>
          </div>
          <p className={styles.reviewHint}>
            Accepting a group imports it and all its children into Chronicle.
            TMDB metadata enrichment runs automatically in the background.
          </p>
          <div className={styles.groupList}>
            {groupResult.groups.map(g => (
              <ScanGroupCard
                key={g.groupKey}
                group={g}
                checked={!rejectedKeys.has(g.groupKey)}
                onToggle={toggleRejected}
              />
            ))}
          </div>
        </div>
      )}

      {/* ── Step 4: Done ─────────────────────────────────────────────────── */}
      {step === 'done' && importResult && (
        <div className={styles.resultCard}>
          <h2 className={styles.resultTitle}>Import complete</h2>
          <p className={styles.reviewHint}>
            TMDB metadata is being downloaded in the background. Check your library in a moment.
          </p>
          <div className={styles.resultStats}>
            <div className={styles.stat}>
              <span className={styles.statValue}>{importResult.imported}</span>
              <span className={styles.statLabel}>Imported</span>
            </div>
            {importResult.duplicates > 0 && (
              <div className={styles.stat}>
                <span className={styles.statValue}>{importResult.duplicates}</span>
                <span className={styles.statLabel}>Already in library</span>
              </div>
            )}
            <div className={styles.stat}>
              <span className={styles.statValue}>{importResult.failed}</span>
              <span className={styles.statLabel}>Failed</span>
            </div>
          </div>
          <button className={styles.scanBtn} onClick={reset}>
            Scan another directory
          </button>
        </div>
      )}
    </div>
  )
}

// ── Utilities ─────────────────────────────────────────────────────────────────

function stepLabel(s: Step): string {
  return { configure: 'Configure', preview: 'Preview', review: 'Review', done: 'Done' }[s]
}

function isStepDone(current: Step, check: Step): boolean {
  const order: Step[] = ['configure', 'preview', 'review', 'done']
  return order.indexOf(current) > order.indexOf(check)
}
