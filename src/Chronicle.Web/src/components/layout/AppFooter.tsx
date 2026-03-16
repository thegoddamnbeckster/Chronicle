import { useState } from 'react'
import { getDiagnostics, DiagnosticsInfo } from '../../api/diagnostics'
import styles from './AppFooter.module.css'

interface AppFooterProps {
  showDiagnostics: boolean
  version?: string
}

export default function AppFooter({ showDiagnostics, version }: AppFooterProps) {
  const [open, setOpen] = useState(false)
  const [diag, setDiag] = useState<DiagnosticsInfo | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function toggle() {
    if (!open && !diag) {
      setLoading(true)
      setError(null)
      try {
        setDiag(await getDiagnostics())
      } catch {
        setError('Failed to load diagnostics.')
      } finally {
        setLoading(false)
      }
    }
    setOpen(o => !o)
  }

  const year = new Date().getFullYear()

  return (
    <footer className={styles.footer}>
      {showDiagnostics && open && (
        <div className={styles.panel}>
          <div className={styles.panelTitle}>Chronicle Dev Environment — Diagnostics</div>
          {loading && <div className={styles.loading}>Loading…</div>}
          {error   && <div className={styles.error}>{error}</div>}
          {diag && (
            <>
              <DiagRow label="Repo root"    value={diag.repoRoot} />
              <DiagRow label="API project"  value={diag.apiProjectPath} />
              <DiagRow label="API dir"      value={diag.apiDir} />
              <div className={styles.diagRow}>
                <span className={styles.diagKey}>Database</span>
                <span className={styles.diagVal}>
                  {diag.dbPath}
                  {diag.dbExists
                    ? <span className={styles.exists}>[EXISTS]</span>
                    : <span className={styles.missing}>[MISSING]</span>}
                </span>
              </div>
              <DiagRow label="Logs"         value={diag.logsPath} />
              <DiagRow label="Branch"       value={`${diag.branch}  (${diag.commitHash})`} />
              <DiagRow label="API"          value={diag.apiUrl} />
              <DiagRow label="Web"          value={diag.webUrl} />
            </>
          )}
        </div>
      )}
      <div className={styles.bar}>
        <span className={styles.symbol}>◆</span>
        <span className={styles.copyright}>© {year} Chronicle</span>
        {version && <span className={styles.version}>· {version}</span>}
        <div className={styles.barRight}>
          {showDiagnostics && (
            <button className={styles.diagTab} onClick={toggle}>
              {open ? '▼' : '▲'} Diagnostics
            </button>
          )}
          <a
            href="https://github.com/thegoddamnbeckster/Chronicle/wiki"
            target="_blank"
            rel="noreferrer"
            className={styles.wikiLink}
          >
            Wiki
          </a>
        </div>
      </div>
    </footer>
  )
}

function DiagRow({ label, value }: { label: string; value: string }) {
  return (
    <div className={styles.diagRow}>
      <span className={styles.diagKey}>{label}</span>
      <span className={styles.diagVal}>{value}</span>
    </div>
  )
}
