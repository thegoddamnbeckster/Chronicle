import { useState, useEffect } from 'react'
import { getServiceStatus, getChangeAccountCommand, ServiceStatus } from '@/api/settings'
import styles from './ServiceSettingsPage.module.css'

type AccountType = 'LocalService' | 'NetworkService' | 'LocalSystem' | 'Custom'

const STATUS_LABELS: Record<string, { label: string; cls: string }> = {
  Running:       { label: 'Running',       cls: 'running' },
  Stopped:       { label: 'Stopped',       cls: 'stopped' },
  NotInstalled:  { label: 'Not Installed', cls: 'notInstalled' },
  NotAvailable:  { label: 'N/A',           cls: 'notInstalled' },
  StartPending:  { label: 'Starting…',     cls: 'pending' },
  StopPending:   { label: 'Stopping…',     cls: 'pending' },
}

export default function ServiceSettingsPage() {
  const [status, setStatus] = useState<ServiceStatus | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  // Change account panel
  const [selectedAccount, setSelectedAccount] = useState<AccountType>('LocalService')
  const [customUsername, setCustomUsername] = useState('')
  const [showAdvanced, setShowAdvanced] = useState(false)
  const [generatedCommand, setGeneratedCommand] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)
  const [cmdLoading, setCmdLoading] = useState(false)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    getServiceStatus()
      .then((s) => { if (!cancelled) { setStatus(s); setLoading(false) } })
      .catch(() => { if (!cancelled) { setError('Could not reach the API.'); setLoading(false) } })
    return () => { cancelled = true }
  }, [])

  const statusInfo = status ? (STATUS_LABELS[status.status] ?? { label: status.status, cls: 'pending' }) : null

  async function handleGenerateCommand() {
    setCmdLoading(true)
    setGeneratedCommand(null)
    try {
      const cmd = await getChangeAccountCommand(
        selectedAccount,
        selectedAccount === 'Custom' ? customUsername : undefined,
      )
      setGeneratedCommand(cmd)
    } catch {
      setGeneratedCommand('Error generating command.')
    } finally {
      setCmdLoading(false)
    }
  }

  function handleCopy() {
    if (!generatedCommand) return
    navigator.clipboard.writeText(generatedCommand).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
    })
  }

  if (loading) return <div className={styles.page}><p className={styles.loading}>Loading service status…</p></div>
  if (error)   return <div className={styles.page}><p className={styles.errorMsg}>{error}</p></div>

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Service</h1>

      {/* ── Status card ─────────────────────────────────────────────────── */}
      <section className={styles.card}>
        <h2 className={styles.cardTitle}>Service Status</h2>

        {!status?.isInstalled && (
          <p className={styles.notInstalledNote}>
            Chronicle is not running as a Windows service. Run{' '}
            <code>scripts\install-service.ps1</code> as Administrator to install it.
            When running on Linux or in Docker, the service panel shows only basic info.
          </p>
        )}

        <div className={styles.statusGrid}>
          <div className={styles.statusRow}>
            <span className={styles.label}>Status</span>
            <span className={`${styles.badge} ${styles[statusInfo?.cls ?? 'pending']}`}>
              {statusInfo?.label}
            </span>
          </div>
          <div className={styles.statusRow}>
            <span className={styles.label}>Service Account</span>
            <span className={styles.value}>{status?.account ?? '—'}</span>
          </div>
          <div className={styles.statusRow}>
            <span className={styles.label}>Start Type</span>
            <span className={styles.value}>{status?.startType ?? '—'}</span>
          </div>
          {status?.uptime && (
            <div className={styles.statusRow}>
              <span className={styles.label}>Uptime</span>
              <span className={styles.value}>{status.uptime}</span>
            </div>
          )}
        </div>
      </section>

      {/* ── Change account ──────────────────────────────────────────────── */}
      {status?.isInstalled && (
        <section className={styles.card}>
          <h2 className={styles.cardTitle}>Change Service Account</h2>
          <p className={styles.hint}>
            Windows does not allow a service to change its own account at runtime.
            Select an account below, copy the generated command, and run it in an
            Administrator PowerShell. Then restart Chronicle.
          </p>

          <div className={styles.radioGroup}>
            {(['LocalService', 'NetworkService', 'LocalSystem'] as AccountType[]).map((acct) => (
              <label key={acct} className={styles.radioLabel}>
                <input
                  type="radio"
                  name="accountType"
                  value={acct}
                  checked={selectedAccount === acct}
                  onChange={() => setSelectedAccount(acct)}
                />
                <span className={styles.radioText}>
                  <strong>{acct}</strong>
                  {acct === 'LocalService'   && ' — Recommended for most users (limited network access)'}
                  {acct === 'NetworkService' && ' — Needed if Chronicle accesses network shares'}
                  {acct === 'LocalSystem'    && ' — Full local access; use only if others fail'}
                </span>
              </label>
            ))}
          </div>

          {/* Advanced toggle */}
          <button
            className={styles.advancedToggle}
            onClick={() => setShowAdvanced((v) => !v)}
          >
            {showAdvanced ? '▼' : '▶'} Advanced: Custom account
          </button>

          {showAdvanced && (
            <div className={styles.advancedPanel}>
              <label className={styles.radioLabel}>
                <input
                  type="radio"
                  name="accountType"
                  value="Custom"
                  checked={selectedAccount === 'Custom'}
                  onChange={() => setSelectedAccount('Custom')}
                />
                <span className={styles.radioText}><strong>Custom account</strong> (domain or local user)</span>
              </label>
              {selectedAccount === 'Custom' && (
                <input
                  type="text"
                  className={styles.textInput}
                  placeholder="DOMAIN\username  or  .\localuser"
                  value={customUsername}
                  onChange={(e) => setCustomUsername(e.target.value)}
                />
              )}
            </div>
          )}

          <button
            className={styles.generateBtn}
            onClick={handleGenerateCommand}
            disabled={cmdLoading || (selectedAccount === 'Custom' && !customUsername.trim())}
          >
            {cmdLoading ? 'Generating…' : 'Generate Command'}
          </button>

          {generatedCommand && (
            <div className={styles.commandBox}>
              <pre className={styles.commandText}>{generatedCommand}</pre>
              <button className={styles.copyBtn} onClick={handleCopy}>
                {copied ? '✓ Copied!' : 'Copy to Clipboard'}
              </button>
              <p className={styles.commandNote}>
                Run the command above in an <strong>Administrator PowerShell</strong>, then
                restart the Chronicle service with <code>Restart-Service Chronicle</code>.
              </p>
            </div>
          )}
        </section>
      )}
    </div>
  )
}
