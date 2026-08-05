import { useState, type FormEvent, type KeyboardEvent } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { useQuery, useMutation } from '@tanstack/react-query'
import { getMediaTypes, createMedia, getCollections, refreshMediaForPlugin } from '@/api/media'
import { listPlugins, previewPluginMetadata } from '@/api/plugins'
import styles from './AddCollectionPage.module.css'

const TMDB_PLUGIN_ID = 'chronicle.plugin.tmdb'

export default function AddCollectionPage() {
  const navigate = useNavigate()

  const [name, setName] = useState('')
  const [overview, setOverview] = useState('')
  const [posterUrl, setPosterUrl] = useState('')
  const [tmdbUrl, setTmdbUrl] = useState('')
  const [mediaTypeId, setMediaTypeId] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  const { data: installedPlugins = [] } = useQuery({
    queryKey: ['plugins'],
    queryFn: listPlugins,
    staleTime: 5 * 60_000,
  })
  const tmdbPlugin = installedPlugins.find(p => p.pluginId === TMDB_PLUGIN_ID)

  // Looks up the pasted URL against TMDB and uses the result to drive (prefill) the Name/
  // Overview/Poster URL fields below, so the user doesn't have to type them by hand or risk
  // a typo diverging from the real collection. The actual external-ID link only gets attached
  // for real once the collection is created (see createMut) — this is preview-only.
  const lookupMut = useMutation({
    mutationFn: () => {
      if (!tmdbPlugin) throw new Error('TMDB plugin is not installed.')
      return previewPluginMetadata(tmdbPlugin.id, tmdbUrl.trim())
    },
    onSuccess: preview => {
      setName(preview.title)
      setOverview(preview.overview ?? '')
      setPosterUrl(preview.posterUrl ?? '')
    },
  })

  function handleTmdbUrlKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Enter' && tmdbUrl.trim()) {
      e.preventDefault()
      lookupMut.mutate()
    }
  }

  const { data: allMediaTypes = [] } = useQuery({
    queryKey: ['media-types'],
    queryFn: getMediaTypes,
    staleTime: 60_000,
  })

  // Collections work for flat (non-hierarchical) media types — a type with a natural multi-level
  // hierarchy (TV Show/Season/Episode, Music Artist/Album/Track, or "anime" itself) already uses
  // parent/child structure for its own seasons/albums, so it can't also be grouped this way.
  // Standalone anime films live on the flat anime_movies type instead.
  const mediaTypes = allMediaTypes.filter(t => t.hierarchyLevels === 1)

  // Default the selector to "Movies" the first time media types load, since that's
  // still the most common case — the user can change it to any other flat type.
  const effectiveMediaTypeId = mediaTypeId
    ?? mediaTypes.find(t => t.name.toLowerCase() === 'movies')?.id
    ?? mediaTypes[0]?.id
    ?? null

  const { data: collections = [], isLoading: collectionsLoading } = useQuery({
    queryKey: ['collections'],
    queryFn: getCollections,
  })

  const createMut = useMutation({
    mutationFn: async () => {
      const item = await createMedia({
        mediaTypeId: effectiveMediaTypeId!,
        name: name.trim(),
        overview: overview.trim() || undefined,
        posterUrl: posterUrl.trim() || undefined,
        hierarchyLevel: 0,
        isCollection: true,
      })
      // Optional: tag the new collection with its real TMDB collection ID and pull the
      // official name/overview/poster, reusing the same Fix Match pathway a regular item
      // uses. This also means future scanned movies belonging to this TMDB collection will
      // recognise it by external ID instead of creating a duplicate stub collection for it.
      if (tmdbUrl.trim()) {
        try {
          return await refreshMediaForPlugin(item.id, TMDB_PLUGIN_ID, tmdbUrl.trim())
        } catch {
          // Collection was still created — just without the TMDB link. Let the user retry
          // via Fix Match on the collection's own page instead of losing their work.
          return item
        }
      }
      return item
    },
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
    if (!effectiveMediaTypeId) {
      setError("Couldn't find any media types — try reloading the page.")
      return
    }
    createMut.mutate()
  }

  return (
    <div className={styles.page}>
      <h2 className={styles.heading}>Add Collection</h2>
      <p className={styles.hint}>
        Creates a new empty collection of the selected media type. It only shows up as a real
        collection card once at least one item of that same type is added to it — do that from
        the collection's own page after creating it.
      </p>

      <form className={styles.form} onSubmit={handleSubmit}>
        <label className={styles.label}>
          TMDB Collection URL <span className={styles.optional}>(optional)</span>
          <div className={styles.urlRow}>
            <input
              className={styles.input}
              type="text"
              value={tmdbUrl}
              onChange={e => setTmdbUrl(e.target.value)}
              onKeyDown={handleTmdbUrlKeyDown}
              placeholder="https://www.themoviedb.org/collection/8864-…"
              autoFocus
            />
            <button
              type="button"
              className={styles.lookupBtn}
              onClick={() => lookupMut.mutate()}
              disabled={lookupMut.isPending || !tmdbUrl.trim() || !tmdbPlugin}
            >
              {lookupMut.isPending ? 'Looking up…' : 'Look Up'}
            </button>
          </div>
        </label>
        <p className={styles.hint}>
          Paste a TMDB collection page link and click Look Up to pull in its official name,
          overview, and poster below — review/edit them, then create. The link is also saved
          on the collection so movies scanned later that belong to it are recognised instead
          of creating a duplicate collection.
        </p>
        {lookupMut.isError && (
          <p className={styles.error}>
            {lookupMut.error instanceof Error ? lookupMut.error.message : 'Lookup failed.'}
          </p>
        )}

        <label className={styles.label}>
          Media Type
          <select
            className={styles.input}
            value={effectiveMediaTypeId ?? ''}
            onChange={e => setMediaTypeId(Number(e.target.value))}
          >
            {mediaTypes.map(t => (
              <option key={t.id} value={t.id}>{t.name}</option>
            ))}
          </select>
        </label>

        <label className={styles.label}>
          Name
          <input
            className={styles.input}
            type="text"
            value={name}
            onChange={e => setName(e.target.value)}
            placeholder="e.g. Three Flavours Cornetto Collection"
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
              <span className={styles.rowCount}>{c.itemCount} item{c.itemCount === 1 ? '' : 's'}</span>
            </Link>
          ))}
        </div>
      )}
    </div>
  )
}
