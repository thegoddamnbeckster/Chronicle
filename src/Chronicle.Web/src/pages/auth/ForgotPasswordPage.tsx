import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { errorText, forgotPassword } from '@/api/auth'
import styles from './Auth.module.css'

export default function ForgotPasswordPage() {
  const [identifier, setIdentifier] = useState('')
  const [done, setDone] = useState<{ emailEnabled: boolean } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setLoading(true)
    try {
      setDone(await forgotPassword(identifier.trim()))
    } catch (err) {
      setError(errorText(err, 'Could not send the request. Try again in a moment.'))
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Chronicle</h1>
        <p className={styles.subtitle}>Reset your password</p>

        {done ? (
          <div className={styles.form}>
            <p>If that account exists and has an email address on file, a reset link is on its way. It works once and expires within the hour.</p>
            {!done.emailEnabled && (
              <p className={styles.error} role="status">
                This Chronicle is not set up to send email, so no message will arrive. Ask an administrator for a reset code instead.
              </p>
            )}
            <Link to="/reset-password">I have a reset code</Link>
            <p className={styles.footer}><Link to="/login">Back to sign in</Link></p>
          </div>
        ) : (
          <form onSubmit={handleSubmit} className={styles.form}>
            <label className={styles.label} htmlFor="fp-id">Username or email address</label>
            <input id="fp-id" value={identifier} onChange={e => setIdentifier(e.target.value)} required autoFocus maxLength={320} />
            {error && <p className={styles.error} role="alert">{error}</p>}
            <button type="submit" className={styles.btn} disabled={loading || !identifier.trim()}>
              {loading ? 'Sending…' : 'Send reset link'}
            </button>
            <p className={styles.footer}>
              <Link to="/reset-password">I have a reset code</Link> · <Link to="/login">Back to sign in</Link>
            </p>
          </form>
        )}
      </div>
    </div>
  )
}
