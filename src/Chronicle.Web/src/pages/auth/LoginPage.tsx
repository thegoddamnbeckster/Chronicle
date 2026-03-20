import { useState, type FormEvent } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { login } from '@/api/auth'
import { useAuth } from '@/hooks/useAuth'
import { useServerReady } from '@/hooks/useServerReady'
import styles from './Auth.module.css'

export default function LoginPage() {
  const navigate = useNavigate()
  const { setUser } = useAuth()
  const serverReady = useServerReady()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setLoading(true)
    try {
      const { token, user } = await login(username, password)
      localStorage.setItem('chronicle_token', token)
      setUser(user)
      navigate('/')
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Login failed')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Chronicle</h1>
        <p className={styles.subtitle}>Sign in to your account</p>
        <form onSubmit={handleSubmit} className={styles.form}>
          <label className={styles.label}>Username</label>
          <input value={username} onChange={e => setUsername(e.target.value)} required autoFocus disabled={!serverReady} />
          <label className={styles.label}>Password</label>
          <input type="password" value={password} onChange={e => setPassword(e.target.value)} required disabled={!serverReady} />
          {error && <p className={styles.error}>{error}</p>}
          <button type="submit" className={styles.btn} disabled={loading || !serverReady}>
            {loading ? 'Signing in…' : 'Sign In'}
          </button>
          {!serverReady && (
            <div className={styles.connectingBar}>
              <span className={styles.connectingDot} />
              Connecting to Chronicle…
            </div>
          )}
        </form>
        <p className={styles.footer}>
          No account? <Link to="/register">Register</Link>
        </p>
      </div>
    </div>
  )
}
