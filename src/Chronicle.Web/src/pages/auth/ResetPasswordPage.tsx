import { useEffect, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { errorText, resetPassword } from '@/api/auth'
import styles from './Auth.module.css'

/** The code arrives in the URL fragment (#token=...), which is never sent to a server. */
function tokenFromHash(hash: string): string {
  const m = /(?:^#|&)token=([^&]+)/.exec(hash)
  return m ? decodeURIComponent(m[1]) : ''
}

export default function ResetPasswordPage() {
  const [token, setToken] = useState(() => tokenFromHash(window.location.hash))
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [changed, setChanged] = useState(false)

  // Once read, take the code out of the address bar so it is not left in history or a screenshot.
  useEffect(() => {
    if (window.location.hash.includes('token=')) {
      window.history.replaceState(null, '', window.location.pathname + window.location.search)
    }
  }, [])

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    if (password.length < 8) { setError('Choose a password of at least 8 characters.'); return }
    if (password !== confirm) { setError('The two passwords do not match.'); return }
    setLoading(true)
    try {
      await resetPassword(token.trim(), password)
      setChanged(true)
    } catch (err) {
      setError(errorText(err, 'Could not change the password.'))
    } finally {
      setLoading(false)
    }
  }

  if (changed) {
    return (
      <div className={styles.page}>
        <div className={styles.card}>
          <h1 className={styles.title}>Chronicle</h1>
          <p className={styles.subtitle}>Password changed</p>
          <p>Your password has been changed and any other signed-in sessions were ended.</p>
          <p className={styles.footer}><Link to="/login">Sign in</Link></p>
        </div>
      </div>
    )
  }

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Chronicle</h1>
        <p className={styles.subtitle}>Choose a new password</p>
        <form onSubmit={handleSubmit} className={styles.form}>
          <label className={styles.label} htmlFor="rp-token">Reset code</label>
          <input id="rp-token" value={token} onChange={e => setToken(e.target.value)} required autoComplete="off" spellCheck={false} />
          <label className={styles.label} htmlFor="rp-new">New password</label>
          <input id="rp-new" type="password" value={password} onChange={e => setPassword(e.target.value)} required autoComplete="new-password" autoFocus={!!token} />
          <label className={styles.label} htmlFor="rp-confirm">Confirm new password</label>
          <input id="rp-confirm" type="password" value={confirm} onChange={e => setConfirm(e.target.value)} required autoComplete="new-password" />
          {error && <p className={styles.error} role="alert">{error}</p>}
          <button type="submit" className={styles.btn} disabled={loading || !token.trim() || !password}>
            {loading ? 'Saving…' : 'Change password'}
          </button>
          <p className={styles.footer}>
            <Link to="/forgot-password">Ask for a new link</Link> · <Link to="/login">Back to sign in</Link>
          </p>
        </form>
      </div>
    </div>
  )
}
