import { useState } from 'react'
import { useQuery, useMutation } from '@tanstack/react-query'
import {
  getImportProviders,
  startAuth,
  pollAuth,
  getAuthStatus,
  importHistory,
  importRatings,
  importWatchlist,
} from '@/api/import'
import { useBackgroundActivity } from '@/contexts/BackgroundActivityContext'
import type { ImportProvider, ImportAuthStart, ImportResult } from '@/types'
import styles from './ImportPage.module.css'

// ── Provider card ─────────────────────────────────────────────────────────────

function ProviderCard({ provider }: { provider: ImportProvider }) {
  const [authFlow, setAuthFlow] = useState<ImportAuthStart | null>(null)
  const [polling, setPolling] = useState(false)
  const [pollError, setPollError] = useState<string | null>(null)
  const [result, setResult] = useState<{ type: string; data: ImportResult } | null>(null)
  const [importing, setImporting] = useState(false)
  const { addJob, completeJob, failJob } = useBackgroundActivity()

  const { data: authenticated, refetch: recheckAuth } = useQuery({
    queryKey: ['import-auth', provider.pluginId],
    queryFn: () => getAuthStatus(provider.pluginId),
  })

  // ── Device auth flow ────────────────────────────────────────────────────────

  const startMut = useMutation({
    mutationFn: () => startAuth(provider.pluginId),
    onSuccess: (data) => {
      setAuthFlow(data)
      setPollError(null)
      startPolling(data.pollCode, data.pollingIntervalSeconds)
    },
    onError: (err: Error) => setPollError(err.message),
  })

  function startPolling(pollCode: string, intervalSec: number) {
    setPolling(true)
    const interval = setInterval(async () => {
      try {
        const result = await pollAuth(provider.pluginId, pollCode)
        if (result.status !== 'pending') {
          clearInterval(interval)
          setPolling(false)
          if (result.status === 'authorized') {
            setAuthFlow(null)
            recheckAuth()
          } else {
            setPollError(
              result.errorMessage ??
                `Auth ${result.status}. Please try again.`,
            )
          }
        }
      } catch {
        clearInterval(interval)
        setPolling(false)
        setPollError('Polling failed — please try again.')
      }
    }, intervalSec * 1000)
  }

  // ── Import triggers ─────────────────────────────────────────────────────────

  async function runImport(type: 'history' | 'ratings' | 'watchlist') {
    const jobId = addJob(`Importing ${type} from ${provider.name}…`)
    setImporting(true)
    setResult(null)
    try {
      let data: ImportResult
      if (type === 'history') data = await importHistory(provider.pluginId)
      else if (type === 'ratings') data = await importRatings(provider.pluginId)
      else data = await importWatchlist(provider.pluginId)
      setResult({ type, data })
      completeJob(jobId, `${data.imported} imported, ${data.skipped} skipped`)
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Import failed'
      setResult({ type, data: { imported: 0, skipped: 0, errors: [msg] } })
      failJob(jobId, msg)
    } finally {
      setImporting(false)
    }
  }

  // ── Render ──────────────────────────────────────────────────────────────────

  return (
    <div className={styles.card}>
      <div className={styles.cardHeader}>
        <div>
          <h3 className={styles.cardTitle}>{provider.name}</h3>
          <p className={styles.cardDesc}>{provider.description}</p>
        </div>
        <div className={styles.cardVersion}>v{provider.version}</div>
      </div>

      <div className={styles.caps}>
        {provider.supportsHistory && <span className={styles.cap}>History</span>}
        {provider.supportsRatings && <span className={styles.cap}>Ratings</span>}
        {provider.supportsWatchlist && <span className={styles.cap}>Watchlist</span>}
      </div>

      {/* Auth section (only for providers that require device auth) */}
      {provider.requiresDeviceAuth && (
        <div className={styles.authSection}>
          {authenticated ? (
            <div className={styles.authStatus}>
              <span className={styles.dot} />
              Connected
            </div>
          ) : authFlow ? (
            <div className={styles.deviceFlow}>
              <p className={styles.deviceInstr}>
                Go to{' '}
                <a
                  href={authFlow.verificationUrl}
                  target="_blank"
                  rel="noopener noreferrer"
                  className={styles.link}
                >
                  {authFlow.verificationUrl}
                </a>{' '}
                and enter the code:
              </p>
              <div className={styles.userCode}>{authFlow.userCode}</div>
              {polling && (
                <p className={styles.polling}>Waiting for authorization…</p>
              )}
              {pollError && <p className={styles.errorMsg}>{pollError}</p>}
            </div>
          ) : (
            <button
              className={styles.authBtn}
              onClick={() => startMut.mutate()}
              disabled={startMut.isPending}
            >
              {startMut.isPending ? 'Starting…' : 'Connect Account'}
            </button>
          )}
          {pollError && !authFlow && (
            <p className={styles.errorMsg}>{pollError}</p>
          )}
        </div>
      )}

      {/* Non-device-auth providers show their status directly */}
      {!provider.requiresDeviceAuth && authenticated && (
        <div className={styles.authStatus}>
          <span className={styles.dot} />
          Ready (configured via plugin settings)
        </div>
      )}

      {!provider.requiresDeviceAuth && !authenticated && (
        <div className={styles.authStatus}>
          <span className={`${styles.dot} ${styles.dotOff}`} />
          Not configured — set your username in{' '}
          <a href="/plugins" className={styles.link}>Plugins → Settings</a>
        </div>
      )}

      {/* Import buttons — shown once authenticated (or for non-auth providers that are ready) */}
      {(authenticated || (!provider.requiresDeviceAuth && authenticated)) && (
        <div className={styles.importButtons}>
          {provider.supportsHistory && (
            <button
              className={styles.importBtn}
              onClick={() => runImport('history')}
              disabled={importing}
            >
              Import History
            </button>
          )}
          {provider.supportsRatings && (
            <button
              className={styles.importBtn}
              onClick={() => runImport('ratings')}
              disabled={importing}
            >
              Import Ratings
            </button>
          )}
          {provider.supportsWatchlist && (
            <button
              className={styles.importBtn}
              onClick={() => runImport('watchlist')}
              disabled={importing}
            >
              Import Watchlist
            </button>
          )}
          {importing && <span className={styles.importing}>Importing…</span>}
        </div>
      )}

      {/* Import result */}
      {result && (
        <div className={result.data.errors.length > 0 ? styles.resultError : styles.resultOk}>
          <strong>{result.type} import complete:</strong>{' '}
          {result.data.imported} imported, {result.data.skipped} skipped
          {result.data.errors.length > 0 && (
            <ul className={styles.errorList}>
              {result.data.errors.slice(0, 5).map((e, i) => (
                <li key={i}>{e}</li>
              ))}
              {result.data.errors.length > 5 && (
                <li>… and {result.data.errors.length - 5} more</li>
              )}
            </ul>
          )}
        </div>
      )}
    </div>
  )
}

// ── Page ──────────────────────────────────────────────────────────────────────

export default function ImportPage() {
  const { data: providers = [], isLoading } = useQuery({
    queryKey: ['import-providers'],
    queryFn: getImportProviders,
  })

  return (
    <div className={styles.page}>
      <h2 className={styles.heading}>Import</h2>
      <p className={styles.subtitle}>
        Import your watch history, ratings and watchlist from external tracking services.
        Each import is incremental — duplicate events are skipped automatically.
      </p>

      {isLoading && <p className={styles.empty}>Loading providers…</p>}

      {!isLoading && providers.length === 0 && (
        <div className={styles.empty}>
          <p>No import plugins loaded.</p>
          <p>
            Install a plugin (Trakt, Simkl, Letterboxd) via{' '}
            <a href="/plugins" className={styles.link}>Plugins</a>.
          </p>
        </div>
      )}

      <div className={styles.grid}>
        {providers.map(p => (
          <ProviderCard key={p.pluginId} provider={p} />
        ))}
      </div>
    </div>
  )
}
