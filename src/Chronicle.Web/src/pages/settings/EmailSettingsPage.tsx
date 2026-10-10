import { useEffect, useState, type FormEvent } from 'react'
import { getEmailSettings, saveEmailSettings, sendTestEmail, type EmailSettings } from '@/api/emailSettings'
import styles from './UsersPage.module.css'

const SECURITY_OPTIONS = [
  { value: 'starttls', label: 'STARTTLS (usually port 587)' },
  { value: 'tls', label: 'TLS from the start (usually port 465)' },
  { value: 'none', label: 'None (only for a trusted local mail server)' },
]

function message(err: unknown, fallback: string): string {
  return err instanceof Error && err.message ? err.message : fallback
}

export default function EmailSettingsPage() {
  const [loaded, setLoaded] = useState<EmailSettings | null>(null)
  const [host, setHost] = useState('')
  const [port, setPort] = useState('587')
  const [security, setSecurity] = useState('starttls')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [clearPassword, setClearPassword] = useState(false)
  const [fromAddress, setFromAddress] = useState('')
  const [fromName, setFromName] = useState('Chronicle')
  const [publicUrl, setPublicUrl] = useState('')
  const [testTo, setTestTo] = useState('')
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

  function apply(s: EmailSettings) {
    setLoaded(s)
    setHost(s.host)
    setPort(String(s.port))
    setSecurity(s.security)
    setUsername(s.username ?? '')
    setFromAddress(s.fromAddress)
    setFromName(s.fromName)
    setPublicUrl(s.publicUrl ?? '')
    setPassword('')
    setClearPassword(false)
  }

  useEffect(() => {
    getEmailSettings().then(apply).catch(err => setError(message(err, 'Could not load the mail settings.')))
  }, [])

  async function handleSave(e: FormEvent) {
    e.preventDefault()
    setBusy('save'); setError(''); setNotice('')
    try {
      const saved = await saveEmailSettings({
        host: host.trim(), port: Number(port), security, username: username.trim(),
        // undefined keeps the saved password; '' removes it.
        password: clearPassword ? '' : (password === '' ? undefined : password),
        fromAddress: fromAddress.trim(), fromName: fromName.trim(), publicUrl: publicUrl.trim(),
      })
      apply(saved)
      setNotice(saved.isConfigured ? 'Saved. Password-reset emails are on.' : 'Saved. Password-reset emails stay off until a server, sender and public address are all set.')
    } catch (err) {
      setError(message(err, 'Could not save.'))
    } finally {
      setBusy('')
    }
  }

  async function handleTest() {
    setBusy('test'); setError(''); setNotice('')
    try {
      await sendTestEmail(testTo.trim())
      setNotice(`A test message was sent to ${testTo.trim()}. Check that it arrives.`)
    } catch (err) {
      setError(message(err, 'The test message could not be sent.'))
    } finally {
      setBusy('')
    }
  }

  if (!loaded) {
    return <div className={styles.page}><h1 className={styles.title}>Email</h1>{error ? <p className={styles.error}>{error}</p> : <p className={styles.loading}>Loading…</p>}</div>
  }

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Email</h1>
      {error && <p className={styles.error} role="alert">{error}</p>}
      {notice && <p className={styles.success}>{notice}</p>}

      <div className={styles.card}>
        <h2 className={styles.cardTitle}>Outgoing mail</h2>
        <p className={styles.hint}>
          Used to send password-reset links. Without it, people who forget a password need an administrator to give them
          a reset code (Settings -&gt; Users). Currently: <strong>{loaded.isConfigured ? 'on' : 'off'}</strong>.
        </p>
        <form onSubmit={handleSave}>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="em-host">Mail server</label>
              <input id="em-host" className={styles.textInput} value={host} onChange={e => setHost(e.target.value)} placeholder="smtp.example.com" />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="em-port">Port</label>
              <input id="em-port" type="number" min={1} max={65535} className={styles.textInput} value={port} onChange={e => setPort(e.target.value)} />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="em-sec">Security</label>
              <select id="em-sec" className={styles.textInput} value={security} onChange={e => setSecurity(e.target.value)}>
                {SECURITY_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
              </select>
            </div>
          </div>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="em-user">User name (if the server needs one)</label>
              <input id="em-user" className={styles.textInput} value={username} onChange={e => setUsername(e.target.value)} autoComplete="off" />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="em-pass">Password</label>
              <input id="em-pass" type="password" className={styles.textInput} value={password} autoComplete="new-password"
                     placeholder={loaded.hasPassword ? 'saved - leave blank to keep' : ''} disabled={clearPassword}
                     onChange={e => setPassword(e.target.value)} />
              {loaded.hasPassword && (
                <label className={styles.checkLabel}>
                  <input type="checkbox" checked={clearPassword} onChange={e => setClearPassword(e.target.checked)} /> Remove the saved password
                </label>
              )}
            </div>
          </div>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="em-from">Send from (address)</label>
              <input id="em-from" className={styles.textInput} value={fromAddress} onChange={e => setFromAddress(e.target.value)} placeholder="chronicle@example.com" />
            </div>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="em-name">Send from (name)</label>
              <input id="em-name" className={styles.textInput} value={fromName} onChange={e => setFromName(e.target.value)} />
            </div>
          </div>
          <div className={styles.formRow}>
            <div className={styles.formGroup}>
              <label className={styles.label} htmlFor="em-url">Chronicle&apos;s public address</label>
              <input id="em-url" className={styles.textInput} value={publicUrl} onChange={e => setPublicUrl(e.target.value)} placeholder="https://chronicle.example.com" />
              <span className={styles.hint}>
                Reset links point here. It is set by you rather than read from the request, so nobody can make Chronicle email
                a link to another site.
              </span>
            </div>
            <button type="submit" className={styles.createBtn} disabled={!!busy}>{busy === 'save' ? 'Saving…' : 'Save'}</button>
          </div>
        </form>
      </div>

      <div className={styles.card}>
        <h2 className={styles.cardTitle}>Send a test message</h2>
        <div className={styles.formRow}>
          <div className={styles.formGroup}>
            <label className={styles.label} htmlFor="em-test">To</label>
            <input id="em-test" type="email" className={styles.textInput} value={testTo} onChange={e => setTestTo(e.target.value)} placeholder="you@example.com" />
          </div>
          <button className={styles.smallBtn} disabled={!!busy || !testTo.trim() || !loaded.isConfigured} onClick={() => void handleTest()}>
            {busy === 'test' ? 'Sending…' : 'Send test'}
          </button>
        </div>
        {!loaded.isConfigured && <p className={styles.hint}>Save a mail server, sender and public address first.</p>}
      </div>
    </div>
  )
}
