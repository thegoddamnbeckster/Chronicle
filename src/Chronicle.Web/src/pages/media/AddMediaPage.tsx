import { useState, useRef, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { getMediaTypes } from '@/api/media'
import { searchMetadata, addFromSearch } from '@/api/scan'
import type { MetadataSearchResult, MediaTypeOption } from '@/types'
import styles from './AddMediaPage.module.css'

// Map Chronicle media type names to the hint passed to the metadata provider's SearchAsync.
// The provider uses this to restrict its search endpoint (e.g. TMDB /search/movie vs /search/tv).
function toMediaTypeHint(mediaTypeName: string): string {
  return mediaTypeName.toLowerCase()
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

  // Auto-select first type when list loads
  useEffect(() => {
    if (mediaTypes.length > 0 && selectedType === null) {
      setSelectedType(mediaTypes[0])
    }
  }, [mediaTypes, selectedType])

  // Debounced search when query or type changes
  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current)

    if (!query.trim() || !selectedType) {
      setResults([])
      setSearchError(null)
      return
    }

    debounceRef.current = setTimeout(async () => {
      setSearching(true)
      setSearchError(null)
      try {
        const hint = toMediaTypeHint(selectedType.name)
        const res = await searchMetadata(query.trim(), hint)
        setResults(res)
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
      const item = await addFromSearch(result.externalId, selectedType.id)
      setAddedIds(prev => new Set(prev).add(result.externalId))
      // Navigate to the new item's detail page
      navigate(`/media/${item.id}`)
    } catch (err) {
      setAddError(err instanceof Error ? err.message : 'Failed to add.')
      setAddingId(null)
    }
  }

  return (
    <div className={styles.page}>
      <h2 className={styles.heading}>Add Media</h2>

      {/* Type + search bar */}
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

      {/* Status line */}
      {searching && <p className={styles.status}>Searching…</p>}
      {!searching && searchError && <p className={styles.noResults}>{searchError}</p>}
      {addError && <p className={styles.error}>{addError}</p>}

      {/* Results grid */}
      {results.length > 0 && (
        <div className={styles.results}>
          {results.map(r => {
            const isAdded = addedIds.has(r.externalId)
            const isAdding = addingId === r.externalId

            return (
              <div key={r.externalId} className={styles.card}>
                <div className={styles.posterWrap}>
                  {r.posterUrl ? (
                    <img
                      src={r.posterUrl}
                      alt={r.title}
                      className={styles.poster}
                      onError={e => {
                        const img = e.currentTarget
                        img.style.display = 'none'
                        const ph = img.nextElementSibling as HTMLElement | null
                        if (ph) ph.style.display = 'flex'
                      }}
                    />
                  ) : null}
                  <div
                    className={styles.posterPlaceholder}
                    style={{ display: r.posterUrl ? 'none' : 'flex' }}
                  >
                    {r.title.charAt(0)}
                  </div>
                </div>

                <div className={styles.cardBody}>
                  <div className={styles.cardTop}>
                    <div>
                      <div className={styles.title}>{r.title}</div>
                      <div className={styles.meta}>
                        {r.year && <span>{r.year}</span>}
                        {r.rating != null && (
                          <span className={styles.rating}>★ {r.rating.toFixed(1)}</span>
                        )}
                      </div>
                    </div>
                    <button
                      className={isAdded ? styles.addedBtn : styles.addBtn}
                      onClick={() => !isAdded && !isAdding && handleAdd(r)}
                      disabled={isAdded || isAdding || !!addingId}
                    >
                      {isAdding ? '…' : isAdded ? 'Added' : '+ Add to Library'}
                    </button>
                  </div>

                  {r.overview && (
                    <p className={styles.overview}>{r.overview}</p>
                  )}
                </div>
              </div>
            )
          })}
        </div>
      )}

      {/* Empty state before searching */}
      {!query.trim() && !searching && (
        <div className={styles.emptyState}>
          <p>Search above to find {selectedType?.displayName.toLowerCase() ?? 'media'} from the metadata scraper.</p>
          <p className={styles.hint}>Results are pulled live from the configured metadata plugin (e.g. TMDB).</p>
        </div>
      )}
    </div>
  )
}
