import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { createMedia } from '@/api/media'
import { addToLibrary } from '@/api/library'
import styles from './AddMediaPage.module.css'

export default function AddMediaPage() {
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const [year, setYear] = useState('')
  const [overview, setOverview] = useState('')
  const [runtime, setRuntime] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    setLoading(true)
    try {
      const item = await createMedia({
        mediaTypeId: 1, // TV shows (Phase 1 only)
        name,
        year: year ? parseInt(year) : undefined,
        overview: overview || undefined,
        runtimeMinutes: runtime ? parseInt(runtime) : undefined,
        hierarchyLevel: 0,
      })
      await addToLibrary(item.id, 'PlanToWatch')
      navigate('/library')
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Failed to add media')
    } finally {
      setLoading(false)
    }
  }

  return (
    <div className={styles.page}>
      <h2 className={styles.heading}>Add TV Show</h2>
      <form onSubmit={handleSubmit} className={styles.form}>
        <label className={styles.label}>Title *</label>
        <input value={name} onChange={e => setName(e.target.value)} required autoFocus placeholder="Breaking Bad" />

        <label className={styles.label}>Year</label>
        <input type="number" value={year} onChange={e => setYear(e.target.value)} placeholder="2008" min="1900" max="2099" />

        <label className={styles.label}>Overview</label>
        <textarea value={overview} onChange={e => setOverview(e.target.value)} rows={4} placeholder="A high school chemistry teacher…" />

        <label className={styles.label}>Runtime (minutes per episode)</label>
        <input type="number" value={runtime} onChange={e => setRuntime(e.target.value)} placeholder="47" min="1" />

        {error && <p className={styles.error}>{error}</p>}

        <div className={styles.actions}>
          <button type="button" className={styles.cancelBtn} onClick={() => navigate(-1)}>Cancel</button>
          <button type="submit" className={styles.submitBtn} disabled={loading}>
            {loading ? 'Adding…' : 'Add to Library'}
          </button>
        </div>
      </form>
    </div>
  )
}
