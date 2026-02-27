import { useState, type FormEvent } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { register } from '@/api/auth'
import styles from './Auth.module.css'

export default function RegisterPage() {
  const navigate = useNavigate()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [email, setEmail] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setLoading(true)
    try {
      const { token } = await register(username, password, email || undefined)
      localStorage.setItem('chronicle_token', token)
      navigate('/')
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Registration failed')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Chronicle</h1>
        <p className={styles.subtitle}>Create your account</p>
        <form onSubmit={handleSubmit} className={styles.form}>
          <label className={styles.label}>Username</label>
          <input value={username} onChange={e => setUsername(e.target.value)} required autoFocus minLength={3} maxLength={50} />
          <label className={styles.label}>Email (optional)</label>
          <input type="email" value={email} onChange={e => setEmail(e.target.value)} />
          <label className={styles.label}>Password</label>
          <input type="password" value={password} onChange={e => setPassword(e.target.value)} required minLength={8} />
          {error && <p className={styles.error}>{error}</p>}
          <button type="submit" className={styles.btn} disabled={loading}>
            {loading ? 'Creating account…' : 'Create Account'}
          </button>
        </form>
        <p className={styles.footer}>
          Already have an account? <Link to="/login">Sign in</Link>
        </p>
      </div>
    </div>
  )
}
