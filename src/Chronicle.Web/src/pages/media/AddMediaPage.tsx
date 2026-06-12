import { useState, useRef, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { getMediaTypes } from '@/api/media'
import { searchMetadata, addFromSearch } from '@/api/scan'
import type { MetadataSearchResult, MediaTypeOption } from '@/types'
import styles from './AddMediaPage.module.css'

function toMediaTypeHint(mediaTypeName: string): string {
  return mediaTypeName.toLowerCase()
}

function titleScore(title: string, q: string): number {
  const t = title.toLowerCase()
  const s = q.toLowerCase()
  if (t === s) return 0
  if (t.startsWith(s)) return 1
  if (t.includes(s)) return 2
  // word boundary match (e.g. query="amazing" matches "The Amazing Race")
  if (t.split(/\s+/).some(w => w.startsWith(s))) return 3
  return 4
}

function rankResults(results: MetadataSearchResult[], query: string): MetadataSearchResult[] {
  return [...results].sort((a, b) => titleScore(a.title, query) - titleScore(b.title, query))
}

function resolveSource(result: MetadataSearchResult): string | null {
  // Prefer the server-populated field; fall back to deriving from externalId format.
  if (result.source) return result.source
  const id = result.externalId.toLowerCase()
  if (id.startsWith('movie:') || id.startsWith('tv:')) return 'tmdb'
  if (id.startsWith('simkl:')) return 'simkl'
  if (id.startsWith('trakt:')) return 'trakt'
  if (id.startsWith('release:') || id.startsWith('release-group:')) return 'musicbrainz'
  if (id.startsWith('hardcover:')) return 'hardcover'
  return null
}

function ResultPoster({ result }: { result: MetadataSearchResult }) {
  const [errored, setErrored] = useState(false)
  const proxied = result.posterUrl
    ? `/api/v1/media/poster-proxy?url=${encodeURIComponent(result.posterUrl)}`
    : null
  if (proxied && !errored) {
    return (
      <img
        src={proxied}
        alt={result.title}
        className={styles.poster}
        onError={() => setErrored(true)}
      />
    )
  }
  return (
    <div className={styles.posterPlaceholder}>
      {result.title.charAt(0).toUpperCase()}
    </div>
  )
}

export default function AddMediaPage() {
  const navigate = useNavigate()

  const [selectedType, setSelectedType] = useState<MediaTypeOption | null>(null)
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<MetadataSearchResult[]>([])
  const [searching, setSearching] = useState(false)
  const [searchError, setSearchError] = useState<string | null>(null)
  const [addingId, setAddingId] = useState<string | null>(null)
  const [addedIds, setAddedIds] = useState<Set<string>>(new Set())
  const [addError, setAddError] = useState<string | null>(null)

  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const { data: mediaTypes = [] } = useQuery({
    queryKey: ['media-types'],
    queryFn: getMediaTypes,
    staleTime: 60_000,
  })

  useEffect(() => {
    if (mediaTypes.length > 0 && selectedType === null) {
      setSelectedType(mediaTypes[0])
    }
  }, [mediaTypes, selectedType])

  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current)

    // Clear immediately so stale results don't linger while debounce is pending
    setResults([])
    setSearchError(null)

    if (!query.trim() || !selectedType) {
      return
    }

    debounceRef.current = setTimeout(async () => {
      setSearching(true)
      setSearchError(null)
      try {
        const hint = toMediaTypeHint(selectedType.name)
        const res = await searchMetadata(query.trim(), hint)
        setResults(rankResults(res, query.trim()))
        if (res.length === 0) setSearchError('No results found.')
      } catch (err) {
        setSearchError(err instanceof Error ? err.message : 'Search failed.')
        setResults([])
      } finally {
        setSearching(false)
      }
    }, 350)

    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current)
    }
  }, [query, selectedType])

  async function handleAdd(result: MetadataSearchResult) {
    if (!selectedType || addingId) return
    setAddingId(result.externalId)
    setAddError(null)
    try {
      const item = await addFromSearch(result.externalId, selectedType.id,
        result.contributingExternalIds ?? undefined)
      setAddedIds(prev => new Set(prev).add(result.externalId))
      navigate(`/media/${item.id}`)
    } catch (err) {
      setAddError(err instanceof Error ? err.message : 'Failed to add.')
      setAddingId(null)
    }
  }

  return (
    <div className={styles.page}>
      <h2 className={styles.heading}>Add Media</h2>

      <div className={styles.searchBar}>
        <div className={styles.typeGroup}>
          {mediaTypes.map(t => (
            <button
              key={t.id}
              className={selectedType?.id === t.id ? styles.typeActive : styles.typeBtn}
              onClick={() => {
                setSelectedType(t)
                setResults([])
                setSearchError(null)
              }}
            >
              {t.displayName}
            </button>
          ))}
        </div>

        <input
          className={styles.searchInput}
          type="text"
          value={query}
          onChange={e => setQuery(e.target.value)}
          placeholder={selectedType ? `Search ${selectedType.displayName.replace(/s$/i, '')}s…` : 'Select a type above…'}
          disabled={!selectedType}
          autoFocus
        />
      </div>

      {searching && <p className={styles.statusSearching}>Searching</p>}
      {!searching && searchError && <p className={styles.noResults}>{searchError}</p>}
      {addError && <p className={styles.error}>{addError}</p>}

      {results.length > 0 && (
        <div className={styles.results}>
          {results.map(r => {
            const libraryItemId = r.libraryItemId ?? null
            const isInLibrary = libraryItemId !== null || addedIds.has(r.externalId)
            const isAdding = addingId === r.externalId
            const displayCast = r.cast?.slice(0, 4) ?? []
            // Use multi-source list when present, otherwise fall back to single source
            const allSources: string[] = r.sources && r.sources.length > 1
              ? r.sources
              : (resolveSource(r) ? [resolveSource(r)!] : [])

            return (
              <div key={r.externalId} className={styles.card}>
                <div className={styles.posterWrap}>
                  <ResultPoster result={r} />
                </div>

                <div className={styles.cardBody}>
                  <div className={styles.cardTop}>
                    <div style={{ minWidth: 0 }}>
                      <div className={styles.title}>{r.title}</div>
                      <div className={styles.meta}>
                        {r.year && <span>{r.year}</span>}
                        {r.rating != null && (
                          <span className={styles.rating}>★ {r.rating.toFixed(1)}</span>
                        )}
                        {allSources.map(s => (
                          <span key={s} className={styles.sourcePill}>{s.toUpperCase()}</span>
                        ))}
                      </div>
                    </div>
                    {isInLibrary ? (
                      <span
                        title={libraryItemId == null ? 'Item was just added — find it in your library' : undefined}
                        style={{ flexShrink: 0 }}
                      >
                        <button
                          className={styles.inLibraryBtn}
                          onClick={() => libraryItemId != null && navigate(`/media/${libraryItemId}`)}
                          disabled={libraryItemId == null}
                          title={libraryItemId != null ? 'Go to item' : undefined}
                          aria-label="In Library"
                        >
                          <span aria-hidden="true">✓</span> In Library
                        </button>
                      </span>
                    ) : (
                    <button
                      className={isAdding ? styles.addedBtn : styles.addBtn}
                      onClick={() => !isAdding && handleAdd(r)}
                      disabled={isAdding || !!addingId}
                    >
                      {isAdding ? '…' : '+ Add to Library'}
                    </button>
                    )}
                  </div>

                  {r.genres && r.genres.length > 0 && (
                    <div className={styles.genres}>
                      {r.genres.slice(0, 5).map(g => (
                        <span key={g} className={styles.genre}>{g}</span>
                      ))}
                    </div>
                  )}

                  {r.overview && (
                    <p className={styles.overview}>{r.overview}</p>
                  )}

                  {displayCast.length > 0 && (
                    <div className={styles.cast}>
                      {displayCast.join(' · ')}
                    </div>
                  )}
                </div>
              </div>
            )
          })}
        </div>
      )}

      {!query.trim() && !searching && (
        <div className={styles.emptyState}>
          <p>Search above to find {selectedType?.displayName.toLowerCase() ?? 'media'} from the metadata scraper.</p>
          <p className={styles.hint}>Results are pulled live from the configured metadata plugin (e.g. TMDB).</p>
        </div>
      )}
    </div>
  )
}
