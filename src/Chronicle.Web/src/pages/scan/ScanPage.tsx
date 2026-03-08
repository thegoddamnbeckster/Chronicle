import { useState } from 'react'
import { useQuery, useMutation } from '@tanstack/react-query'
import { getScanStatus, previewScan, identifyFiles, importApproved } from '@/api/scan'
import { getMediaTypes } from '@/api/media'
import { useBackgroundActivity } from '@/contexts/BackgroundActivityContext'
import type {
  ScannedFile,
  FileIdentification,
  MetadataCandidate,
  MediaTypeOption,
} from '@/types'
import styles from './ScanPage.module.css'

type Step = 'configure' | 'preview' | 'identify' | 'review' | 'done'

export default function ScanPage() {
  // ── Configuration state ──────────────────────────────────────────────────
  const [path, setPath] = useState('')
  const [recursive, setRecursive] = useState(true)
  const [mediaTypeId, setMediaTypeId] = useState<number | ''>('')

  // ── Pipeline state ───────────────────────────────────────────────────────
  const [step, setStep] = useState<Step>('configure')
  const [previewFiles, setPreviewFiles] = useState<ScannedFile[]>([])
  const [identifications, setIdentifications] = useState<FileIdentification[]>([])
  // Map of filePath → chosen externalId (undefined = skipped)
  const [approvals, setApprovals] = useState<Record<string, string | undefined>>({})
  const [importResult, setImportResult] = useState<{ imported: number; failed: number } | null>(null)
  const [error, setError] = useState<string | null>(null)

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
      setError(null)
      setStep('preview')
    },
    onError: (err: Error) => setError(err.message),
  })

  const identifyMut = useMutation({
    mutationFn: () =>
      identifyFiles({ files: previewFiles, mediaTypeId: Number(mediaTypeId) }),
    onMutate: () => addJob(`Identifying ${previewFiles.length} files…`),
    onSuccess: (data, _vars, jobId) => {
      setIdentifications(data.results)
      // Pre-select the top candidate for each file if score >= 60
      const auto: Record<string, string | undefined> = {}
      for (const id of data.results) {
        const top = id.candidates[0]
        auto[id.file.filePath] = top && top.matchScore >= 60 ? top.externalId : undefined
      }
      setApprovals(auto)
      setError(null)
      setStep('review')
      completeJob(jobId as string, 'Identification complete')
    },
    onError: (err: Error, _vars, jobId) => {
      setError(err.message)
      failJob(jobId as string, err.message)
    },
  })

  const importMut = useMutation({
    mutationFn: () => {
      const approved = Object.entries(approvals)
        .filter(([, extId]) => extId !== undefined)
        .map(([filePath, externalId]) => ({ filePath, externalId: externalId! }))
      if (approved.length === 0) throw new Error('No files approved for import.')
      return importApproved({ approvals: approved, mediaTypeId: Number(mediaTypeId) })
    },
    onMutate: () => {
      const count = Object.values(approvals).filter(Boolean).length
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

  // ── Helpers ───────────────────────────────────────────────────────────────
  const selectCandidate = (filePath: string, externalId: string | undefined) => {
    setApprovals((prev) => ({ ...prev, [filePath]: externalId }))
  }

  const approvedCount = Object.values(approvals).filter(Boolean).length
  const canScan = path.trim() !== '' && mediaTypeId !== '' && !previewMut.isPending

  // ── Render ────────────────────────────────────────────────────────────────
  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1 className={styles.title}>File Scan</h1>
        {step !== 'configure' && step !== 'done' && (
          <button
            className={styles.resetBtn}
            onClick={() => { setStep('configure'); setPreviewFiles([]); setIdentifications([]); setApprovals({}); setError(null) }}
          >
            Start over
          </button>
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
        </div>
      )}

      {/* ── Step 2: Preview ──────────────────────────────────────────────── */}
      {step === 'preview' && (
        <div className={styles.resultCard}>
          <div className={styles.resultHeader}>
            <h2 className={styles.resultTitle}>Found {previewFiles.length} files</h2>
            <button
              className={styles.scanBtn}
              disabled={previewFiles.length === 0 || identifyMut.isPending}
              onClick={() => identifyMut.mutate()}
            >
              {identifyMut.isPending ? 'Identifying…' : `Identify with TMDB →`}
            </button>
          </div>

          <table className={styles.skippedTable}>
            <thead>
              <tr>
                <th>Parsed title</th>
                <th>Year</th>
                <th>Confidence</th>
                <th>File</th>
              </tr>
            </thead>
            <tbody>
              {previewFiles.map((f, i) => (
                <tr key={i}>
                  <td>{f.parsedTitle}</td>
                  <td>{f.parsedYear ?? '—'}</td>
                  <td className={styles.confidence}>{f.confidenceScore}%</td>
                  <td className={styles.filePath}>{f.filePath}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* ── Step 3: Review / Approve ─────────────────────────────────────── */}
      {step === 'review' && (
        <div className={styles.resultCard}>
          <div className={styles.resultHeader}>
            <h2 className={styles.resultTitle}>
              Review matches — {approvedCount} of {identifications.length} approved
            </h2>
            <button
              className={styles.scanBtn}
              disabled={approvedCount === 0 || importMut.isPending}
              onClick={() => importMut.mutate()}
            >
              {importMut.isPending ? 'Importing…' : `Import ${approvedCount} items →`}
            </button>
          </div>

          <div className={styles.identifyList}>
            {identifications.map((id) => (
              <IdentifyRow
                key={id.file.filePath}
                identification={id}
                selectedId={approvals[id.file.filePath]}
                onSelect={(extId) => selectCandidate(id.file.filePath, extId)}
              />
            ))}
          </div>
        </div>
      )}

      {/* ── Step 4: Done ─────────────────────────────────────────────────── */}
      {step === 'done' && importResult && (
        <div className={styles.resultCard}>
          <h2 className={styles.resultTitle}>Import complete</h2>
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
          <button
            className={styles.scanBtn}
            onClick={() => { setStep('configure'); setPreviewFiles([]); setIdentifications([]); setApprovals({}); setImportResult(null); setError(null) }}
          >
            Scan another directory
          </button>
        </div>
      )}
    </div>
  )
}

// ── Sub-component: one file + its candidates ──────────────────────────────────

interface IdentifyRowProps {
  identification: FileIdentification
  selectedId: string | undefined
  onSelect: (externalId: string | undefined) => void
}

function IdentifyRow({ identification, selectedId, onSelect }: IdentifyRowProps) {
  const { file, candidates } = identification

  return (
    <div className={styles.identifyRow}>
      <div className={styles.identifyFile}>
        <span className={styles.identifyTitle}>{file.parsedTitle}</span>
        {file.parsedYear && <span className={styles.identifyYear}>({file.parsedYear})</span>}
        <span className={styles.confidence}>{file.confidenceScore}%</span>
        <span className={styles.filePath}>{file.filePath}</span>
      </div>

      {candidates.length === 0 ? (
        <p className={styles.noMatch}>No matches found — file will be skipped.</p>
      ) : (
        <div className={styles.candidateList}>
          {/* Skip option */}
          <label className={`${styles.candidate} ${selectedId === undefined ? styles.candidateSelected : ''}`}>
            <input
              type="radio"
              name={file.filePath}
              checked={selectedId === undefined}
              onChange={() => onSelect(undefined)}
            />
            <span className={styles.candidateSkip}>Skip this file</span>
          </label>

          {candidates.map((c) => (
            <CandidateRow
              key={c.externalId}
              candidate={c}
              filePath={file.filePath}
              selected={selectedId === c.externalId}
              onSelect={() => onSelect(c.externalId)}
            />
          ))}
        </div>
      )}
    </div>
  )
}

interface CandidateRowProps {
  candidate: MetadataCandidate
  filePath: string
  selected: boolean
  onSelect: () => void
}

function CandidateRow({ candidate, filePath, selected, onSelect }: CandidateRowProps) {
  return (
    <label className={`${styles.candidate} ${selected ? styles.candidateSelected : ''}`}>
      <input
        type="radio"
        name={filePath}
        checked={selected}
        onChange={onSelect}
      />
      {candidate.posterUrl && (
        <img className={styles.poster} src={candidate.posterUrl} alt={candidate.title} loading="lazy" />
      )}
      <div className={styles.candidateInfo}>
        <span className={styles.candidateTitle}>{candidate.title}</span>
        {candidate.year && <span className={styles.candidateYear}>({candidate.year})</span>}
        {candidate.rating != null && (
          <span className={styles.candidateRating}>{candidate.rating.toFixed(1)}</span>
        )}
        {candidate.overview && (
          <p className={styles.candidateOverview}>{candidate.overview.slice(0, 140)}{candidate.overview.length > 140 ? '…' : ''}</p>
        )}
      </div>
      <span className={`${styles.matchScore} ${scoreClass(candidate.matchScore)}`}>
        {candidate.matchScore}%
      </span>
    </label>
  )
}

// ── Utilities ─────────────────────────────────────────────────────────────────

function stepLabel(s: 'configure' | 'preview' | 'review' | 'done'): string {
  return { configure: 'Configure', preview: 'Preview', review: 'Review', done: 'Done' }[s]
}

function isStepDone(current: Step, check: 'configure' | 'preview' | 'review' | 'done'): boolean {
  const order: Step[] = ['configure', 'preview', 'identify', 'review', 'done']
  return order.indexOf(current) > order.indexOf(check)
}

function scoreClass(score: number): string {
  if (score >= 80) return styles.scoreHigh
  if (score >= 50) return styles.scoreMid
  return styles.scoreLow
}
