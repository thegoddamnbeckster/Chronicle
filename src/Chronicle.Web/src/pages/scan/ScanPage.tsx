import { useState, useEffect, useRef } from 'react'
import { useQuery, useMutation } from '@tanstack/react-query'
import { getScanStatus, getScanProgress, previewScan, importDirect } from '@/api/scan'
import type { ScanProgress } from '@/api/scan'
import { getMediaTypes } from '@/api/media'
import { useBackgroundActivity } from '@/contexts/BackgroundActivityContext'
import type { ScannedFile, MediaTypeOption } from '@/types'
import styles from './ScanPage.module.css'

type Step = 'configure' | 'preview' | 'review' | 'done'

export default function ScanPage() {
  // ── Configuration state ──────────────────────────────────────────────────
  const [path, setPath] = useState('')
  const [recursive, setRecursive] = useState(true)
  const [mediaTypeId, setMediaTypeId] = useState<number | ''>('')

  // ── Pipeline state ───────────────────────────────────────────────────────
  const [step, setStep] = useState<Step>('configure')
  const [previewFiles, setPreviewFiles] = useState<ScannedFile[]>([])
  // Set of file paths the user has unchecked (skipped). All checked by default.
  const [skipped, setSkipped] = useState<Set<string>>(new Set())
  const [importResult, setImportResult] = useState<{ imported: number; failed: number } | null>(null)
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
      return previewScan({ path: path.trim(), recursive, mediaTypeId: Number(mediaTypeId) })
    },
    onSuccess: (data) => {
      setPreviewFiles(data.files)
      setSkipped(new Set())
      setError(null)
      setStep('preview')
    },
    onError: (err: Error) => setError(err.message),
  })

  const importMut = useMutation({
    mutationFn: () => {
      const toImport = previewFiles.filter((f) => !skipped.has(f.filePath))
      if (toImport.length === 0) throw new Error('No files selected for import.')
      return importDirect({
        files: toImport.map((f) => ({
          filePath: f.filePath,
          parsedTitle: f.parsedTitle,
          parsedYear: f.parsedYear ?? null,
          suggestedExternalId: f.suggestedExternalId ?? null,
          mediaTypeHint: f.mediaTypeHint,
        })),
        mediaTypeId: Number(mediaTypeId),
      })
    },
    onMutate: () => {
      const count = previewFiles.filter((f) => !skipped.has(f.filePath)).length
      return addJob(`Importing ${count} items…`)
    },
    onSuccess: (data, _vars, jobId) => {
      setImportResult({ imported: data.imported, failed: data.failed })
      setError(null)
      setStep('done')
      completeJob(jobId as string, `${data.imported} imported`)
    },
    onError: (err: Error, _vars, jobId) => {
      setError(err.message)
      failJob(jobId as string, err.message)
    },
  })

  // ── Scan progress polling ────────────────────────────────────────────────
  // While a preview scan is in-flight, poll /scan/progress every 500 ms and
  // display the folder being scanned below the button.
  useEffect(() => {
    if (previewMut.isPending) {
      setScanProgress(null)
      pollRef.current = setInterval(async () => {
        try {
          const p = await getScanProgress()
          setScanProgress(p)
        } catch {
          // ignore polling errors — the main mutation error handler covers failures
        }
      }, 500)
    } else {
      if (pollRef.current) {
        clearInterval(pollRef.current)
        pollRef.current = null
      }
      // Keep the last progress visible briefly, then clear
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
  const toggleSkip = (filePath: string) => {
    setSkipped((prev) => {
      const next = new Set(prev)
      next.has(filePath) ? next.delete(filePath) : next.add(filePath)
      return next
    })
  }

  const approvedCount = previewFiles.filter((f) => !skipped.has(f.filePath)).length
  const canScan = path.trim() !== '' && mediaTypeId !== '' && !previewMut.isPending

  function reset() {
    setStep('configure')
    setPreviewFiles([])
    setSkipped(new Set())
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
            <input
              id="scan-path"
              className={styles.textInput}
              type="text"
              placeholder="C:\Movies or /mnt/media/movies"
              value={path}
              onChange={(e) => setPath(e.target.value)}
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
      {step === 'preview' && (
        <div className={styles.resultCard}>
          <div className={styles.resultHeader}>
            <h2 className={styles.resultTitle}>Found {previewFiles.length} files</h2>
            <button
              className={styles.scanBtn}
              disabled={previewFiles.length === 0}
              onClick={() => setStep('review')}
            >
              Review {previewFiles.length} files →
            </button>
          </div>

          <table className={styles.skippedTable}>
            <thead>
              <tr>
                <th>Parsed title</th>
                <th>Year</th>
                <th>Type</th>
                <th>Confidence</th>
                <th>File</th>
              </tr>
            </thead>
            <tbody>
              {previewFiles.map((f, i) => (
                <tr key={i}>
                  <td>{f.parsedTitle}</td>
                  <td>{f.parsedYear ?? '—'}</td>
                  <td><span className={styles.mediaTypeBadge}>{f.mediaTypeHint}</span></td>
                  <td className={styles.confidence}>{f.confidenceScore}%</td>
                  <td className={styles.filePath}>{f.filePath}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* ── Step 3: Review ───────────────────────────────────────────────── */}
      {step === 'review' && (
        <div className={styles.resultCard}>
          <div className={styles.resultHeader}>
            <h2 className={styles.resultTitle}>
              {approvedCount} of {previewFiles.length} selected for import
            </h2>
            <div className={styles.headerActions}>
              <button
                className={styles.secondaryBtn}
                onClick={() => setSkipped(new Set(previewFiles.map((f) => f.filePath)))}
              >
                Deselect all
              </button>
              <button
                className={styles.secondaryBtn}
                onClick={() => setSkipped(new Set())}
              >
                Select all
              </button>
              <button
                className={styles.scanBtn}
                disabled={approvedCount === 0 || importMut.isPending}
                onClick={() => importMut.mutate()}
              >
                {importMut.isPending ? 'Importing…' : `Import ${approvedCount} items →`}
              </button>
            </div>
          </div>

          <p className={styles.reviewHint}>
            All files are selected by default. Uncheck any you want to skip.
            TMDB metadata will be downloaded automatically in the background after import.
          </p>

          <div className={styles.reviewList}>
            {previewFiles.map((f) => {
              const checked = !skipped.has(f.filePath)
              return (
                <label
                  key={f.filePath}
                  className={`${styles.reviewRow} ${!checked ? styles.reviewRowSkipped : ''}`}
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => toggleSkip(f.filePath)}
                    className={styles.reviewCheck}
                  />
                  <div className={styles.reviewContent}>
                    <div className={styles.reviewInfo}>
                      <span className={styles.identifyTitle}>{f.parsedTitle}</span>
                      {f.parsedYear && <span className={styles.identifyYear}>({f.parsedYear})</span>}
                      <span className={styles.mediaTypeBadge}>{f.mediaTypeHint}</span>
                      <span className={styles.confidence}>{f.confidenceScore}%</span>
                      {f.suggestedExternalId && (
                        <span className={styles.nfoTag} title="External ID from NFO sidecar">NFO</span>
                      )}
                    </div>
                    <div className={styles.reviewPath}>{f.filePath}</div>
                  </div>
                </label>
              )
            })}
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
