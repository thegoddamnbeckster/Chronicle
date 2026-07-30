import { useState, type FormEvent } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { useQuery, useMutation } from '@tanstack/react-query'
import { getMediaTypes, createMedia, getCollections } from '@/api/media'
import styles from './AddCollectionPage.module.css'

export default function AddCollectionPage() {
  const navigate = useNavigate()

  const [name, setName] = useState('')
  const [overview, setOverview] = useState('')
  const [posterUrl, setPosterUrl] = useState('')
  const [error, setError] = useState<string | null>(null)

  const { data: mediaTypes = [] } = useQuery({
    queryKey: ['media-types'],
    queryFn: getMediaTypes,
    staleTime: 60_000,
  })
  const moviesType = mediaTypes.find(t => t.name.toLowerCase() === 'movies') ?? null

  const { data: collections = [], isLoading: collectionsLoading } = useQuery({
    queryKey: ['collections'],
    queryFn: getCollections,
  })

  const createMut = useMutation({
    mutationFn: () => createMedia({
      mediaTypeId: moviesType!.id,
      name: name.trim(),
      overview: overview.trim() || undefined,
      posterUrl: posterUrl.trim() || undefined,
      hierarchyLevel: 0,
    }),
    onSuccess: item => navigate(`/media/${item.id}`),
    onError: err => setError(err instanceof Error ? err.message : 'Failed to create collection.'),
  })

  function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    if (!name.trim()) {
      setError('Name is required.')
      return
    }
    if (!moviesType) {
      setError("Couldn't find the movies media type — try reloading the page.")
      return
    }
    createMut.mutate()
  }

  return (
    <div className={styles.page}>
      <h2 className={styles.heading}>Add Movie Collection</h2>
      <p className={styles.hint}>
        Creates a new empty collection. It only shows up as a real collection card once at
        least one movie is added to it — do that from the collection's own page after creating it.
      </p>

      <form className={styles.form} onSubmit={handleSubmit}>
        <label className={styles.label}>
          Name
          <input
            className={styles.input}
            type="text"
            value={name}
            onChange={e => setName(e.target.value)}
            placeholder="e.g. Three Flavours Cornetto Collection"
            autoFocus
          />
        </label>

        <label className={styles.label}>
          Overview <span className={styles.optional}>(optional)</span>
          <textarea
            className={styles.textarea}
            value={overview}
            onChange={e => setOverview(e.target.value)}
            rows={3}
          />
        </label>

        <label className={styles.label}>
          Poster URL <span className={styles.optional}>(optional)</span>
          <input
            className={styles.input}
            type="text"
            value={posterUrl}
            onChange={e => setPosterUrl(e.target.value)}
            placeholder="https://…"
          />
        </label>

        {error && <p className={styles.error}>{error}</p>}

        <button className={styles.submitBtn} type="submit" disabled={createMut.isPending}>
          {createMut.isPending ? 'Creating…' : 'Create Collection'}
        </button>
      </form>

      <h3 className={styles.subheading}>Existing Collections</h3>
      {collectionsLoading && <p className={styles.hint}>Loading…</p>}
      {!collectionsLoading && collections.length === 0 && (
        <p className={styles.hint}>No collections yet.</p>
      )}
      {collections.length > 0 && (
        <div className={styles.list}>
          {collections.map(c => (
            <Link key={c.id} to={`/media/${c.id}`} className={styles.row}>
              {c.posterUrl
                ? <img src={c.posterUrl} alt="" className={styles.rowPoster} />
                : <div className={styles.rowPosterPlaceholder}>{c.name.charAt(0)}</div>}
              <span className={styles.rowName}>{c.name}</span>
              <span className={styles.rowCount}>{c.movieCount} movie{c.movieCount === 1 ? '' : 's'}</span>
            </Link>
          ))}
        </div>
      )}
    </div>
  )
}
