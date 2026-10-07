import { useCallback, useEffect, useRef, useState } from 'react'
import {
  RESTORE_CONFIRMATION,
  createBackup,
  deleteBackup,
  downloadBackup,
  formatBytes,
  getDatabaseStatus,
  listBackups,
  restoreBackup,
  runMaintenance,
  saveDatabaseSettings,
  uploadBackup,
  type BackupInfo,
  type DatabaseStatus,
} from '@/api/database'
import styles from './UsersPage.module.css'

const KIND_LABEL: Record<string, string> = {
  scheduled: 'Nightly',
  manual: 'Manual',
  'pre-restore': 'Before a restore',
  uploaded: 'Uploaded',
}

function when(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString()
}

function message(err: unknown, fallback: string): string {
  return err instanceof Error && err.message ? err.message : fallback
}

/** Polls the health endpoint until the API answers again, then sends the user to sign in. */
async function waitForRestart(): Promise<void> {
  // Give the old process a moment to actually exit so we don't see it answer one last time.
  await new Promise(r => setTimeout(r, 4000))
  for (let i = 0; i < 90; i++) {
    try {
      const res = await fetch('/api/health')
      if (res.ok) break
    } catch {
      /* still down */
    }
    await new Promise(r => setTimeout(r, 2000))
  }
  localStorage.removeItem('chronicle_token')
  window.location.href = '/login'
}

export default function DatabasePage() {
  const [status, setStatus] = useState<DatabaseStatus | null>(null)
  const [backups, setBackups] = useState<BackupInfo[]>([])
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState('')

  const [keep, setKeep] = useState('')
  const [warnMb, setWarnMb] = useState('')

  const [restoring, setRestoring] = useState<BackupInfo | null>(null)
  const [typed, setTyped] = useState('')
  const [restarting, setRestarting] = useState(false)

  const [uploadPct, setUploadPct] = useState<number | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  const load = useCallback(async () => {
    try {
      const [s, b] = await Promise.all([getDatabaseStatus(), listBackups()])
      setStatus(s)
      setBackups(b)
      setKeep(String(s.retainCount))
      setWarnMb(String(Math.round(s.warnSizeBytes / 1024 / 1024)))
      setError('')
    } catch (err) {
      setError(message(err, 'Could not load the database status.'))
    }
  }, [])

  useEffect(() => { void load() }, [load])

  async function run(label: string, action: () => Promise<string | void>) {
    setBusy(label)
    setError('')
    setNotice('')
    try {
      const result = await action()
      if (result) setNotice(result)
      await load()
    } catch (err) {
      setError(message(err, `${label} did not work.`))
    } finally {
      setBusy('')
    }
  }

  async function handleUpload(file: File | undefined) {
    if (!file) return
    setUploadPct(0)
    await run('Upload', async () => {
      try {
        const info = await uploadBackup(file, setUploadPct)
        return `Uploaded and checked ${info.fileName}. It is ready to restore.`
      } finally {
        setUploadPct(null)
        if (fileInput.current) fileInput.current.value = ''
      }
    })
  }

  async function confirmRestore() {
    if (!restoring) return
    setBusy('Restore')
    setError('')
    try {
      await restoreBackup(restoring.fileName, typed)
      setRestarting(true)
      void waitForRestart()
    } catch (err) {
      setError(message(err, 'The restore did not start.'))
      setBusy('')
    }
  }

  if (restarting) {
    return (
      <div className={styles.page}>
        <h1 className={styles.title}>Database</h1>
        <div className={styles.card}>
          <h2 className={styles.cardTitle}>Restoring…</h2>
          <p className={styles.hint}>
            The backup is staged and Chronicle is restarting to put it in place. You will be sent to the sign-in page
            when it is back. If it does not come back by itself within a couple of minutes, start Chronicle again
            (in Docker this happens automatically).
          </p>
        </div>
      </div>
    )
  }

  if (!status) {
    return (
      <div className={styles.page}>
        <h1 className={styles.title}>Database</h1>
        {error ? <p className={styles.error}>{error}</p> : <p className={styles.loading}>Loading…</p>}
      </div>
    )
  }

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Database</h1>

      {error && <p className={styles.error}>{error}</p>}
      {notice && <p className={styles.success}>{notice}</p>}

      {!status.supported ? (
        <div className={styles.card}>
          <h2 className={styles.cardTitle}>Not available</h2>
          <p className={styles.hint}>{status.note}</p>
        </div>
      ) : (
        <>
          {status.overWarnSize && (
            <p className={styles.error} role="alert">
              The database is {formatBytes(status.databaseBytes + status.walBytes)}, over the {formatBytes(status.warnSizeBytes)} warning
              size. Run a full rebuild to reclaim free space, or consider moving to PostgreSQL.
            </p>
          )}

          <div className={styles.card}>
            <h2 className={styles.cardTitle}>Status</h2>
            <dl>
              <dt>Database file</dt><dd>{status.databaseFile}</dd>
              <dt>Size</dt>
              <dd>
                {formatBytes(status.databaseBytes)}
                {status.walBytes > 0 && <> (+ {formatBytes(status.walBytes)} write-ahead log)</>}
              </dd>
              <dt>Free space inside the file</dt>
              <dd>{formatBytes(status.reclaimableBytes)} could be reclaimed by a full rebuild</dd>
              <dt>Free disk space</dt><dd>{formatBytes(status.freeDiskBytes)}</dd>
              <dt>Schema version</dt><dd>{status.latestMigration ?? 'unknown'}</dd>
              <dt>Last backup</dt><dd>{status.lastBackupAtUtc ? when(status.lastBackupAtUtc) : 'never'}</dd>
            </dl>
            {status.largestTables.length > 0 && (
              <>
                <h3 className={styles.detailHeading}>Largest tables</h3>
                <ul>
                  {status.largestTables.map(t => (
                    <li key={t.name}>{t.name} - {formatBytes(t.bytes)}</li>
                  ))}
                </ul>
              </>
            )}
          </div>

          <div className={styles.card}>
            <h2 className={styles.cardTitle}>Settings</h2>
            <div className={styles.formRow}>
              <div className={styles.formGroup}>
                <label className={styles.label} htmlFor="db-keep">Backups to keep</label>
                <input id="db-keep" type="number" min={1} max={365} className={styles.textInput}
                       value={keep} onChange={e => setKeep(e.target.value)} />
              </div>
              <div className={styles.formGroup}>
                <label className={styles.label} htmlFor="db-warn">Warn when larger than (MB)</label>
                <input id="db-warn" type="number" min={1} className={styles.textInput}
                       value={warnMb} onChange={e => setWarnMb(e.target.value)} />
              </div>
              <button className={styles.createBtn} disabled={!!busy}
                      onClick={() => void run('Save', async () => {
                        await saveDatabaseSettings({ backupsToKeep: Number(keep), warnSizeMb: Number(warnMb) })
                        return 'Settings saved.'
                      })}>
                Save
              </button>
            </div>
            <p className={styles.hint}>
              The nightly backup, the weekly quick maintenance and the monthly full rebuild are scheduled tasks; change
              their times or run them now on Background Tasks.
            </p>
          </div>

          <div className={styles.card}>
            <div className={styles.cardHeader}>
              <h2 className={styles.cardTitle}>Backups ({backups.length})</h2>
              <div className={styles.userActions}>
                <button className={styles.createBtn} disabled={!!busy}
                        onClick={() => void run('Backup', async () => {
                          const info = await createBackup()
                          return `Backed up to ${info.fileName} (${formatBytes(info.sizeBytes)}).`
                        })}>
                  {busy === 'Backup' ? 'Backing up…' : 'Back up now'}
                </button>
                <button className={styles.smallBtn} disabled={!!busy} onClick={() => fileInput.current?.click()}>
                  Upload a backup…
                </button>
                <input ref={fileInput} type="file" accept=".zip,application/zip" hidden
                       data-testid="backup-file"
                       onChange={e => void handleUpload(e.target.files?.[0])} />
              </div>
            </div>
            <p className={styles.hint}>
              Stored beside the database in <code>{status.backupDirectory}</code>. Copy them somewhere else too: a backup on
              the same disk does not protect against that disk failing. A backup holds everything in the database -
              accounts (password hashes), API key hashes and plugin credentials (stored as entered) - so treat the files
              as sensitive and keep them somewhere only you can read.
            </p>
            {uploadPct !== null && <p className={styles.hint}>Uploading… {uploadPct}%</p>}

            {backups.length === 0 ? (
              <p className={styles.hint}>No backups yet.</p>
            ) : backups.map(b => (
              <div key={b.fileName} className={styles.userRow}>
                <div className={styles.userInfo}>
                  <span className={styles.userName}>{b.fileName}</span>
                  <span className={styles.userMeta}>
                    {KIND_LABEL[b.kind] ?? b.kind} · {when(b.createdAtUtc)} · {formatBytes(b.sizeBytes)}
                    {b.rowCounts?.users !== undefined && <> · {b.rowCounts.users} users, {b.rowCounts.media_items ?? 0} items</>}
                  </span>
                </div>
                <div className={styles.userActions}>
                  <button className={styles.smallBtn} disabled={!!busy}
                          onClick={() => void run('Download', async () => { await downloadBackup(b.fileName) })}>
                    Download
                  </button>
                  <button className={styles.smallBtn} disabled={!!busy}
                          onClick={() => { setRestoring(b); setTyped('') }}>
                    Restore…
                  </button>
                  <button className={styles.dangerBtn} disabled={!!busy}
                          onClick={() => { if (window.confirm(`Delete ${b.fileName}?`)) void run('Delete', async () => { await deleteBackup(b.fileName) }) }}>
                    Delete
                  </button>
                </div>
              </div>
            ))}

            {restoring && (
              <div className={styles.detail} role="dialog" aria-label="Confirm restore">
                <h3 className={styles.detailHeading}>Restore {restoring.fileName}?</h3>
                <p className={styles.error}>
                  This replaces everything in Chronicle with the contents of this backup
                  (taken {when(restoring.createdAtUtc)}). Anything added or changed since then is lost, everyone is signed out,
                  and Chronicle restarts. A safety backup of the current data is made first.
                </p>
                <div className={styles.formRow}>
                  <div className={styles.formGroup}>
                    <label className={styles.label} htmlFor="db-restore-confirm">Type {RESTORE_CONFIRMATION} to confirm</label>
                    <input id="db-restore-confirm" className={styles.textInput} value={typed}
                           onChange={e => setTyped(e.target.value)} autoComplete="off" />
                  </div>
                  <button className={styles.dangerBtn} disabled={typed !== RESTORE_CONFIRMATION || !!busy}
                          onClick={() => void confirmRestore()}>
                    {busy === 'Restore' ? 'Restoring…' : 'Restore and restart'}
                  </button>
                  <button className={styles.smallBtn} disabled={!!busy} onClick={() => setRestoring(null)}>Cancel</button>
                </div>
              </div>
            )}
          </div>

          <div className={styles.card}>
            <h2 className={styles.cardTitle}>Maintenance</h2>
            <p className={styles.hint}>
              Chronicle looks after the database by itself on a schedule. You can also run it now.
            </p>
            <div className={styles.userActions}>
              <button className={styles.smallBtn} disabled={!!busy}
                      onClick={() => void run('Quick maintenance', async () => (await runMaintenance('quick')).summary)}>
                {busy === 'Quick maintenance' ? 'Working…' : 'Quick maintenance'}
              </button>
              <button className={styles.smallBtn} disabled={!!busy}
                      onClick={() => {
                        if (window.confirm('A full rebuild locks the database while it runs and can take a while on a large library. Continue?'))
                          void run('Full rebuild', async () => (await runMaintenance('full')).summary)
                      }}>
                {busy === 'Full rebuild' ? 'Rebuilding…' : 'Full rebuild'}
              </button>
            </div>
            <p className={styles.hint}>
              Quick: refreshes query statistics and trims the write-ahead log. Full: rebuilds indexes and compacts the file to
              reclaim free space; needs free disk space about the size of the database.
            </p>
          </div>
        </>
      )}
    </div>
  )
}
