import { useRef, useState } from 'react'
import { useQuery, useMutation } from '@tanstack/react-query'
import { getScanStatus, runScan } from '@/api/scan'
import { getMediaTypes } from '@/api/media'
import { useBackgroundActivity } from '@/contexts/BackgroundActivityContext'
import type { FileScanResult, MediaTypeOption } from '@/types'
import styles from './ScanPage.module.css'

export default function ScanPage() {
  const [path, setPath] = useState('')
  const [recursive, setRecursive] = useState(true)
  const [mediaTypeId, setMediaTypeId] = useState<number | ''>('')
  const [threshold, setThreshold] = useState(80)
  const [result, setResult] = useState<FileScanResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const jobIdRef = useRef<string | null>(null)
  const { addJob, completeJob, failJob } = useBackgroundActivity()

  const { data: status } = useQuery({
    queryKey: ['scan-status'],
    queryFn: getScanStatus,
  })

  const { data: allMediaTypes = [] } = useQuery({
    queryKey: ['media-types'],
    queryFn: getMediaTypes,
  })

  // Filter to only media types the scanner supports
  const supportedTypes: MediaTypeOption[] = allMediaTypes.filter((t) =>
    status?.supportedMediaTypeNames.includes(t.name),
  )

  const scanMut = useMutation({
    mutationFn: () => {
      if (!mediaTypeId) throw new Error('Please select a media type.')
      return runScan({ path, recursive, mediaTypeId: Number(mediaTypeId), confidenceThreshold: threshold })
    },
    onMutate: () => {
      const label = `Scanning ${path.trim() || 'directory'}…`
      jobIdRef.current = addJob(label)
    },
    onSuccess: (data) => {
      setResult(data)
      setError(null)
      if (jobIdRef.current) {
        completeJob(jobIdRef.current, `${data.added} added, ${data.skipped} skipped`)
        jobIdRef.current = null
      }
    },
    onError: (err: Error) => {
      setError(err.message)
      setResult(null)
      if (jobIdRef.current) {
        failJob(jobIdRef.current, err.message)
        jobIdRef.current = null
      }
    },
  })

  const canScan = path.trim() !== '' && mediaTypeId !== '' && !scanMut.isPending

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1 className={styles.title}>File Scan</h1>
      </div>

      <div className={styles.formCard}>
        <div className={styles.field}>
          <label className={styles.label} htmlFor="scan-path">
            Directory path
          </label>
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
            <label className={styles.label} htmlFor="media-type">
              Media type
            </label>
            <select
              id="media-type"
              className={styles.select}
              value={mediaTypeId}
              onChange={(e) => setMediaTypeId(e.target.value === '' ? '' : Number(e.target.value))}
            >
              <option value="">— select type —</option>
              {supportedTypes.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.displayName}
                </option>
              ))}
            </select>
          </div>

          <div className={styles.field}>
            <label className={styles.label} htmlFor="threshold">
              Confidence threshold: <strong>{threshold}%</strong>
            </label>
            <input
              id="threshold"
              className={styles.slider}
              type="range"
              min={0}
              max={100}
              step={5}
              value={threshold}
              onChange={(e) => setThreshold(Number(e.target.value))}
            />
            <div className={styles.sliderHint}>
              Files below this confidence level will not be added.
            </div>
          </div>
        </div>

        <div className={styles.checkRow}>
          <label className={styles.checkLabel}>
            <input
              type="checkbox"
              checked={recursive}
              onChange={(e) => setRecursive(e.target.checked)}
            />
            Include subdirectories
          </label>
        </div>

        {error && <p className={styles.errorMsg}>{error}</p>}

        <button
          className={styles.scanBtn}
          disabled={!canScan}
          onClick={() => { setResult(null); scanMut.mutate() }}
        >
          {scanMut.isPending ? 'Scanning…' : 'Start Scan'}
        </button>
      </div>

      {result && (
        <div className={styles.resultCard}>
          <h2 className={styles.resultTitle}>Scan complete</h2>
          <div className={styles.resultStats}>
            <div className={styles.stat}>
              <span className={styles.statValue}>{result.added}</span>
              <span className={styles.statLabel}>Added</span>
            </div>
            <div className={styles.stat}>
              <span className={styles.statValue}>{result.alreadyInLibrary}</span>
              <span className={styles.statLabel}>Already in library</span>
            </div>
            <div className={styles.stat}>
              <span className={styles.statValue}>{result.skipped}</span>
              <span className={styles.statLabel}>Below threshold</span>
            </div>
          </div>

          {result.skippedFiles.length > 0 && (
            <div className={styles.skippedSection}>
              <h3 className={styles.skippedTitle}>
                Files below threshold ({result.skippedFiles.length})
              </h3>
              <table className={styles.skippedTable}>
                <thead>
                  <tr>
                    <th>Parsed title</th>
                    <th>Confidence</th>
                    <th>File</th>
                  </tr>
                </thead>
                <tbody>
                  {result.skippedFiles.map((f, i) => (
                    <tr key={i}>
                      <td>{f.parsedTitle}</td>
                      <td className={styles.confidence}>{f.confidenceScore}%</td>
                      <td className={styles.filePath}>{f.filePath}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
