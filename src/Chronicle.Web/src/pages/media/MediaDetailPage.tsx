import { useState, useRef, useEffect } from 'react'
import { useParams, useNavigate, Link, useLocation } from 'react-router-dom'
import tmdbLogoFallback from '@/assets/tmdb-logo.svg'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getMedia, getMediaChildren, refreshMedia, reidentifyMedia, clearMediaExternalId, suppressMediaMatch, deleteMedia } from '@/api/media'
import { getLibrary, addToLibrary, updateLibraryEntry } from '@/api/library'
import type { LibraryStatus } from '@/types'
import styles from './MediaDetailPage.module.css'

const STATUS_OPTIONS: LibraryStatus[] = [
  'Unwatched', 'PlanToWatch', 'Watching', 'Completed', 'Dropped', 'OnHold', 'Rewatching',
]

/** Returns a display label for a LibraryStatus value that is appropriate for the given media type. */
function getStatusLabel(status: LibraryStatus, mediaTypeName: string): string {
  const t = mediaTypeName.toLowerCase()
  const isMusic = t === 'music' || t === 'podcast' || t === 'podcasts' || t === 'audiobook' || t === 'audiobooks'
  const isBook = t === 'book' || t === 'books'
  const isGame = t === 'game' || t === 'games'

  switch (status) {
    case 'PlanToWatch':
      if (isMusic) return 'Plan to Listen'
      if (isBook) return 'Plan to Read'
      if (isGame) return 'Plan to Play'
      return 'Plan to Watch'
    case 'Watching':
      if (isMusic) return 'Listening'
      if (isBook) return 'Reading'
      if (isGame) return 'Playing'
      return 'Watching'
    case 'Rewatching':
      if (isMusic) return 'Re-listening'
      if (isBook) return 'Re-reading'
      if (isGame) return 'Replaying'
      return 'Rewatching'
    case 'Unwatched':
      if (isMusic) return 'Unlistened'
      if (isBook) return 'Unread'
      if (isGame) return 'Unplayed'
      return 'Unwatched'
    case 'Completed': return 'Completed'
    case 'Dropped': return 'Dropped'
    case 'OnHold': return 'On Hold'
    default: return status
  }
}

/** Returns the label for the "Plan to Watch / Listen / Read / Play" quick-add button. */
function getPlanToLabel(mediaTypeName: string): string {
  return getStatusLabel('PlanToWatch', mediaTypeName)
}

export default function MediaDetailPage() {
  const { id } = useParams<{ id: string }>()
  const mediaId = Number(id)
  const navigate = useNavigate()
  const location = useLocation()
  const qc = useQueryClient()

  const navState = (location.state as { listIds?: number[]; listLabel?: string } | null) ?? null
  const listIds = navState?.listIds ?? []
  const currentIndex = listIds.indexOf(mediaId)
  const prevId = currentIndex > 0 ? listIds[currentIndex - 1] : null
  const nextId = currentIndex < listIds.length - 1 ? listIds[currentIndex + 1] : null

  const { data: item, isLoading, error } = useQuery({
    queryKey: ['media', mediaId],
    queryFn: () => getMedia(mediaId),
    enabled: !isNaN(mediaId),
  })

  const { data: children = [] } = useQuery({
    queryKey: ['media', mediaId, 'children'],
    queryFn: () => getMediaChildren(mediaId),
    enabled: !isNaN(mediaId),
  })

  // Get the user's library entry for this item (if any)
  const { data: library = [] } = useQuery({
    queryKey: ['library'],
    queryFn: () => getLibrary(),
  })
  const libraryEntry = library.find(e => e.mediaItem.id === mediaId) ?? null

  const addMut = useMutation({
    mutationFn: (status: LibraryStatus) => addToLibrary(mediaId, status),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['library'] }),
  })

  const updateMut = useMutation({
    mutationFn: ({ status, rating }: { status?: LibraryStatus; rating?: number }) =>
      updateLibraryEntry(libraryEntry!.id, { status, userRating: rating }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['library'] }),
  })

  const refreshMut = useMutation({
    mutationFn: () => refreshMedia(mediaId),
    onSuccess: (updated) => {
      qc.setQueryData(['media', mediaId], updated)
      qc.invalidateQueries({ queryKey: ['library'] })
    },
  })

  const [fixMatchOpen, setFixMatchOpen] = useState(false)
  const [fixMatchInput, setFixMatchInput] = useState('')
  const fixMatchInputRef = useRef<HTMLInputElement>(null)

  const reidentifyMut = useMutation({
    mutationFn: () => reidentifyMedia(mediaId, fixMatchInput),
    onSuccess: (updated) => {
      qc.setQueryData(['media', mediaId], updated)
      qc.invalidateQueries({ queryKey: ['library'] })
      setFixMatchOpen(false)
      setFixMatchInput('')
    },
  })

  const clearMatchMut = useMutation({
    mutationFn: () => clearMediaExternalId(mediaId, 'tmdb'),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['media', mediaId] })
      qc.invalidateQueries({ queryKey: ['library'] })
    },
  })

  const suppressMatchMut = useMutation({
    mutationFn: () => suppressMediaMatch(mediaId, 'tmdb'),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['media', mediaId] })
      qc.invalidateQueries({ queryKey: ['library'] })
    },
  })

  const [deleteConfirm, setDeleteConfirm] = useState(false)

  const deleteMut = useMutation({
    mutationFn: () => deleteMedia(mediaId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['library'] })
      navigate('/library')
    },
  })

  useEffect(() => {
    if (fixMatchOpen) fixMatchInputRef.current?.focus()
  }, [fixMatchOpen])

  const tmdbIds = item?.externalIds.filter(e => e.source === 'tmdb') ?? []
  const otherIds = item?.externalIds.filter(e => e.source !== 'tmdb') ?? []
  const tmdbSuppressed = tmdbIds.some(e => e.externalId === '__suppress__')
  const tmdbHasRealId = tmdbIds.some(e => e.externalId !== '__suppress__')

  // TMDB only supports movies and TV. Show the TMDB box when the item already
  // has a TMDB match (so the user can clear a wrong match), OR when the media
  // type is one that TMDB actually handles.
  const TMDB_SUPPORTED_TYPES = ['movie', 'movies', 'tv', 'tv shows']
  const isTmdbSupported =
    Boolean(item?.tmdbMeta) ||
    TMDB_SUPPORTED_TYPES.includes((item?.mediaTypeName ?? '').toLowerCase())

  if (isLoading) return <div className={styles.page}><p className={styles.loading}>Loading…</p></div>
  if (error || !item) {
    return (
      <div className={styles.page}>
        <p className={styles.error}>Media not found.</p>
        <button className={styles.backBtn} onClick={() => navigate(-1)}>← Back</button>
      </div>
    )
  }

  const hasBackdrop = Boolean(item.tmdbMeta?.backdropUrl)

  return (
    <div className={styles.page}>
      <div className={`${styles.backdropSection}${hasBackdrop ? ` ${styles.backdropActive}` : ''}`}>
        {hasBackdrop && (
          <div
            className={styles.backdropImg}
            style={{ backgroundImage: `url("${item.tmdbMeta!.backdropUrl}")` }}
            aria-hidden
          />
        )}
        <div className={`${styles.backdropContent}${hasBackdrop ? ` ${styles.backdropContentActive}` : ''}`}>
      <div className={`${styles.topNav}${hasBackdrop ? ` ${styles.topNavBoxed}` : ''}`}>
        <button className={styles.backBtn} onClick={() => navigate(-1)}>← Back</button>
        {(item.ancestors && item.ancestors.length > 0) ? (
          <>
            <nav className={styles.breadcrumb}>
              <Link to="/library" className={styles.breadcrumbLink}>Library</Link>
              {item.ancestors.map(a => (
                <span key={a.id} className={styles.breadcrumbItem}>
                  <span className={styles.breadcrumbSep}>›</span>
                  <Link to={`/media/${a.id}`} className={styles.breadcrumbLink}>{a.name}</Link>
                </span>
              ))}
            </nav>
            {(() => {
              const parent = item.ancestors![item.ancestors!.length - 1]
              return (
                <Link to={`/media/${parent.id}`} className={styles.upBtn}>
                  ↑ {parent.name}
                </Link>
              )
            })()}
          </>
        ) : (
          <Link to="/library" className={styles.upBtn}>↑ Library</Link>
        )}
        {listIds.length > 0 && (
          <div className={styles.listNav}>
            {prevId != null ? (
              <Link to={`/media/${prevId}`} state={navState} className={styles.navBtn}>‹ Prev</Link>
            ) : (
              <span className={`${styles.navBtn} ${styles.navBtnDisabled}`}>‹ Prev</span>
            )}
            <span className={styles.navPos}>
              {navState?.listLabel && <span className={styles.navLabel}>{navState.listLabel} · </span>}
              {currentIndex + 1} / {listIds.length}
            </span>
            {nextId != null ? (
              <Link to={`/media/${nextId}`} state={navState} className={styles.navBtn}>Next ›</Link>
            ) : (
              <span className={`${styles.navBtn} ${styles.navBtnDisabled}`}>Next ›</span>
            )}
          </div>
        )}
      </div>

      <div className={styles.hero}>
        <div className={styles.posterWrap}>
          {item.posterUrl
            ? (
              <img
                className={styles.poster}
                src={item.posterUrl}
                alt={item.name}
                onError={e => {
                  const img = e.currentTarget
                  img.style.display = 'none'
                  const placeholder = img.nextElementSibling as HTMLElement | null
                  if (placeholder) placeholder.style.display = 'flex'
                }}
              />
            )
            : null
          }
          <div
            className={styles.posterPlaceholder}
            style={{ display: item.posterUrl ? 'none' : 'flex' }}
          >
            {item.name.charAt(0)}
          </div>
        </div>

        <div className={`${styles.meta}${hasBackdrop ? ` ${styles.metaBoxed}` : ''}`}>
          <h1 className={styles.title}>{item.name}</h1>

          <div className={styles.deleteArea}>
            {!deleteConfirm ? (
              <button className={styles.deleteBtn} onClick={() => setDeleteConfirm(true)}>
                Delete
              </button>
            ) : (
              <div className={styles.deleteConfirmStrip}>
                <span className={styles.deleteConfirmText}>
                  Delete <strong>{item.name}</strong>? This cannot be undone.
                </span>
                <button className={styles.deleteConfirmCancel} onClick={() => setDeleteConfirm(false)}>
                  Cancel
                </button>
                <button
                  className={styles.deleteConfirmOk}
                  onClick={() => deleteMut.mutate()}
                  disabled={deleteMut.isPending}
                >
                  {deleteMut.isPending ? 'Deleting…' : 'Delete'}
                </button>
              </div>
            )}
          </div>

          <div className={styles.metaRow}>
            {item.year && <span className={styles.chip}>{item.year}</span>}
            <span className={styles.chip}>{item.mediaTypeName}</span>
            {item.runtimeMinutes && (
              <span className={styles.chip}>{item.runtimeMinutes} min</span>
            )}
          </div>

          {item.overview && <p className={styles.overview}>{item.overview}</p>}

          {/* TMDB metadata box — only shown for TMDB-supported types (movies/TV),
              or when the item already has a TMDB match (to allow clearing a wrong match) */}
          {isTmdbSupported && (
            <div className={styles.metadataBox}>
              <div className={styles.metadataBoxHeader}>
                <div className={styles.metadataBoxBrand}>
                  <img
                    src="https://www.themoviedb.org/assets/2/v4/logos/v2/blue_short-8e7b30f73a4020692ccca9c88bafe5dcb20f201ad3a6b4d0b6dcea5b0b95d9f3.svg"
                    alt="TMDB"
                    className={styles.tmdbLogo}
                    onError={(e) => { e.currentTarget.src = tmdbLogoFallback; }}
                  />
                  {(() => {
                    const tmdbLog = item.refreshLogs?.find(l => l.providerName === 'TMDB')
                    if (!tmdbLog) return null
                    const dt = new Date(tmdbLog.refreshedAt)
                    const label = tmdbLog.succeeded
                      ? `Last refreshed ${dt.toLocaleDateString()} ${dt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`
                      : `Last refresh failed: ${tmdbLog.errorMessage ?? 'unknown error'}`
                    return <p className={styles.refreshTimestamp}>{label}</p>
                  })()}
                </div>
                <div className={styles.metadataBoxActions}>
                  {item.hierarchyLevel === 0 && (
                    <>
                      <button
                        className={styles.fixMatchBtn}
                        onClick={() => { setFixMatchOpen(v => !v); reidentifyMut.reset() }}
                        title="Manually specify the correct TMDB match"
                      >
                        ✎ Fix Match
                      </button>
                      {tmdbHasRealId && (
                        <button
                          className={styles.clearMatchBtn}
                          onClick={() => clearMatchMut.mutate()}
                          disabled={clearMatchMut.isPending}
                          title="Remove the TMDB match — refresh will attempt a new auto-search next cycle"
                        >
                          {clearMatchMut.isPending ? 'Clearing…' : '✕ Clear Match'}
                        </button>
                      )}
                      {tmdbSuppressed ? (
                        <button
                          className={styles.resumeMatchBtn}
                          onClick={() => clearMatchMut.mutate()}
                          disabled={clearMatchMut.isPending}
                          title="Re-enable auto-matching for this item"
                        >
                          {clearMatchMut.isPending ? 'Resuming…' : '↺ Resume Auto-Match'}
                        </button>
                      ) : !tmdbHasRealId && (
                        <button
                          className={styles.suppressMatchBtn}
                          onClick={() => suppressMatchMut.mutate()}
                          disabled={suppressMatchMut.isPending}
                          title="Mark as unmatched — refresh will never auto-search for this item again"
                        >
                          {suppressMatchMut.isPending ? 'Suppressing…' : '⊘ No Match'}
                        </button>
                      )}
                    </>
                  )}
                  {item.hierarchyLevel > 0 && (
                    <>
                      {item.tmdbMeta && (
                        <span className={styles.inheritedLabel}>Metadata from parent show</span>
                      )}
                      {tmdbHasRealId && (
                        <button
                          className={styles.clearMatchBtn}
                          onClick={() => clearMatchMut.mutate()}
                          disabled={clearMatchMut.isPending}
                          title="Remove the stale TMDB match from this item"
                        >
                          {clearMatchMut.isPending ? 'Clearing…' : '✕ Clear Match'}
                        </button>
                      )}
                    </>
                  )}
                  <button
                    className={styles.refreshBtn}
                    onClick={() => refreshMut.mutate()}
                    disabled={refreshMut.isPending}
                    title={item.hierarchyLevel > 0 ? 'Refresh poster and metadata from parent show' : undefined}
                  >
                    {refreshMut.isPending ? 'Refreshing…' : '↻ Refresh'}
                  </button>
                </div>
              </div>

              {fixMatchOpen && (
                <div className={styles.fixMatchPanel}>
                  <p className={styles.fixMatchHint}>
                    Enter a TMDB ID, typed ID, or URL:
                    <code> 1159831</code> · <code>movie:1159831</code> · <code>tv:1396</code> ·
                    <code> https://www.themoviedb.org/movie/1159831</code>
                  </p>
                  <div className={styles.fixMatchRow}>
                    <input
                      ref={fixMatchInputRef}
                      className={styles.fixMatchInput}
                      type="text"
                      placeholder="TMDB ID or URL…"
                      value={fixMatchInput}
                      onChange={e => { setFixMatchInput(e.target.value); reidentifyMut.reset() }}
                      onKeyDown={e => {
                        if (e.key === 'Enter' && fixMatchInput.trim()) reidentifyMut.mutate()
                        if (e.key === 'Escape') { setFixMatchOpen(false); setFixMatchInput('') }
                      }}
                    />
                    <button
                      className={styles.fixMatchApplyBtn}
                      onClick={() => reidentifyMut.mutate()}
                      disabled={reidentifyMut.isPending || !fixMatchInput.trim()}
                    >
                      {reidentifyMut.isPending ? 'Applying…' : 'Apply'}
                    </button>
                  </div>
                  {reidentifyMut.isError && (
                    <p className={styles.refreshError}>
                      {(reidentifyMut.error as Error).message}
                    </p>
                  )}
                </div>
              )}

              <div className={styles.tmdbGrid}>
                {/* Rating — show-level OR season/episode vote average */}
                {(item.tmdbMeta?.rating != null || item.tmdbMeta?.voteAverage != null) && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Rating</span>
                    <span className={styles.tmdbValue}>
                      {((item.tmdbMeta?.rating ?? item.tmdbMeta?.voteAverage) as number).toFixed(1)}&thinsp;/&thinsp;10
                    </span>
                  </div>
                )}

                {/* Air date (seasons and episodes) */}
                {item.tmdbMeta?.airDate && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Air Date</span>
                    <span className={styles.tmdbValue}>{item.tmdbMeta.airDate}</span>
                  </div>
                )}

                {/* Episode count (seasons) */}
                {item.tmdbMeta?.episodeCount != null && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Episodes</span>
                    <span className={styles.tmdbValue}>{item.tmdbMeta.episodeCount}</span>
                  </div>
                )}

                {item.tmdbMeta?.directors && item.tmdbMeta.directors.length > 0 && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>
                      {item.tmdbMeta.directors.length === 1 ? 'Director' : 'Directors'}
                    </span>
                    <span className={styles.tmdbValue}>
                      {item.tmdbMeta.directors.join(', ')}
                    </span>
                  </div>
                )}

                {/* Episode crew (directors/writers) */}
                {item.tmdbMeta?.crew && item.tmdbMeta.crew.length > 0 && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Crew</span>
                    <span className={styles.tmdbValue}>{item.tmdbMeta.crew.join(', ')}</span>
                  </div>
                )}

                {item.tmdbMeta?.genres && item.tmdbMeta.genres.length > 0 && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Genres</span>
                    <div className={styles.tmdbTags}>
                      {item.tmdbMeta.genres.map(g => (
                        <span key={g} className={styles.tmdbTag}>{g}</span>
                      ))}
                    </div>
                  </div>
                )}

                {item.tmdbMeta?.cast && item.tmdbMeta.cast.length > 0 && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Cast</span>
                    <span className={styles.tmdbValue}>
                      {item.tmdbMeta.cast.slice(0, 6).join(', ')}
                    </span>
                  </div>
                )}

                {/* Episode guest stars */}
                {item.tmdbMeta?.guestStars && item.tmdbMeta.guestStars.length > 0 && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Guest Stars</span>
                    <span className={styles.tmdbValue}>
                      {item.tmdbMeta.guestStars.slice(0, 6).join(', ')}
                    </span>
                  </div>
                )}

                {tmdbHasRealId && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>ID</span>
                    <div className={styles.externalIds}>
                      {tmdbIds.filter(e => e.externalId !== '__suppress__').map(eid => (
                        <span key={eid.externalId} className={styles.externalIdChip}>
                          <span className={styles.externalIdValue}>{eid.externalId}</span>
                        </span>
                      ))}
                    </div>
                  </div>
                )}

                {/* Image thumbnails — click opens full size in new tab */}
                {(() => {
                  const tmdb = item.tmdbMeta
                  // Resolve image URLs: prefer full URLs, fall back to path-based construction
                  const posterUrl = tmdb?.posterUrl
                  const backdropUrl = tmdb?.backdropUrl
                  const seasonPosterUrl = tmdb?.posterPath
                    ? `https://image.tmdb.org/t/p/w500${tmdb.posterPath}`
                    : null
                  const stillUrl = tmdb?.stillPath
                    ? `https://image.tmdb.org/t/p/w500${tmdb.stillPath}`
                    : null
                  const hasImages = posterUrl || backdropUrl || seasonPosterUrl || stillUrl
                  if (!hasImages) return null
                  return (
                  <div className={`${styles.tmdbRow} ${styles.tmdbRowImages}`}>
                    <span className={styles.tmdbLabel}>Images</span>
                    <div className={styles.tmdbImageLinks}>
                      {posterUrl && (
                        <a href={posterUrl} target="_blank" rel="noreferrer"
                          className={styles.tmdbImageLink} title="Open full-size poster">
                          <img src={posterUrl} alt="Poster" className={styles.tmdbThumbnail}
                            onError={e => { e.currentTarget.style.display = 'none' }} />
                          <span className={styles.tmdbThumbnailLabel}>Poster ↗</span>
                        </a>
                      )}
                      {seasonPosterUrl && !posterUrl && (
                        <a href={seasonPosterUrl} target="_blank" rel="noreferrer"
                          className={styles.tmdbImageLink} title="Open full-size season poster">
                          <img src={seasonPosterUrl} alt="Season Poster" className={styles.tmdbThumbnail}
                            onError={e => { e.currentTarget.style.display = 'none' }} />
                          <span className={styles.tmdbThumbnailLabel}>Season Poster ↗</span>
                        </a>
                      )}
                      {stillUrl && (
                        <a href={stillUrl} target="_blank" rel="noreferrer"
                          className={styles.tmdbImageLink} title="Open full-size episode still">
                          <img src={stillUrl} alt="Episode Still" className={styles.tmdbThumbnail}
                            onError={e => { e.currentTarget.style.display = 'none' }} />
                          <span className={styles.tmdbThumbnailLabel}>Still ↗</span>
                        </a>
                      )}
                      {backdropUrl && (
                        <a href={backdropUrl} target="_blank" rel="noreferrer"
                          className={styles.tmdbImageLink} title="Open full-size backdrop">
                          <img src={backdropUrl} alt="Backdrop" className={styles.tmdbThumbnail}
                            onError={e => { e.currentTarget.style.display = 'none' }} />
                          <span className={styles.tmdbThumbnailLabel}>Backdrop ↗</span>
                        </a>
                      )}
                    </div>
                  </div>
                  )
                })()}
              </div>

              {refreshMut.isError && (
                <p className={styles.refreshError}>
                  {`Refresh failed: ${(refreshMut.error as Error).message}`}
                </p>
              )}
            </div>
          )}

          {/* Other external IDs */}
          {otherIds.length > 0 && (
            <div className={styles.externalIds}>
              {otherIds.map(eid => (
                <span key={`${eid.source}-${eid.externalId}`} className={styles.externalIdChip}>
                  <span className={styles.externalIdSource}>{eid.source}</span>
                  <span className={styles.externalIdValue}>{eid.externalId}</span>
                </span>
              ))}
            </div>
          )}

          {/* File Scanner box — show whenever the item came from the scanner */}
          {item.fileScannerMeta &&
            (item.fileScannerMeta.filePath ||
              item.fileScannerMeta.localPosterPath ||
              item.fileScannerMeta.nfoPosterUrl ||
              item.fileScannerMeta.importedAt) && (
            <div className={styles.scannerBox}>
              <div className={styles.scannerHeader}>File Scanner</div>
              <div className={styles.tmdbGrid}>
                {item.fileScannerMeta.filePath && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>File</span>
                    <span className={styles.scannerPath}>{item.fileScannerMeta.filePath}</span>
                  </div>
                )}
                {!item.fileScannerMeta.filePath && item.fileScannerMeta.importedAt && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Imported</span>
                    <span className={styles.scannerPath}>
                      {new Date(item.fileScannerMeta.importedAt).toLocaleString()}
                    </span>
                  </div>
                )}
                {item.fileScannerMeta.localPosterPath && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Poster</span>
                    <span className={styles.scannerPath}>{item.fileScannerMeta.localPosterPath}</span>
                  </div>
                )}
                {item.fileScannerMeta.nfoPosterUrl && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>NFO</span>
                    {item.fileScannerMeta.nfoPosterUrl.startsWith('http') ? (
                      <a
                        href={item.fileScannerMeta.nfoPosterUrl}
                        target="_blank"
                        rel="noreferrer"
                        className={styles.tmdbImageLink}
                      >
                        {item.fileScannerMeta.nfoPosterUrl}
                      </a>
                    ) : (
                      <span className={styles.scannerPath}>{item.fileScannerMeta.nfoPosterUrl}</span>
                    )}
                  </div>
                )}
              </div>
            </div>
          )}

          {/* Library actions */}
          <div className={styles.librarySection}>
            {libraryEntry ? (
              <div className={styles.libraryControls}>
                <label className={styles.label}>Status</label>
                <select
                  className={styles.select}
                  value={libraryEntry.status}
                  onChange={e =>
                    updateMut.mutate({ status: e.target.value as LibraryStatus })
                  }
                >
                  {STATUS_OPTIONS.map(s => (
                    <option key={s} value={s}>{getStatusLabel(s, item.mediaTypeName)}</option>
                  ))}
                </select>

                <label className={styles.label}>Your Rating</label>
                <select
                  className={styles.select}
                  value={libraryEntry.userRating ?? ''}
                  onChange={e =>
                    updateMut.mutate({
                      rating: e.target.value ? Number(e.target.value) : undefined,
                    })
                  }
                >
                  <option value="">Not rated</option>
                  {[...Array(10)].map((_, i) => (
                    <option key={i + 1} value={i + 1}>{i + 1}</option>
                  ))}
                </select>
              </div>
            ) : (
              <div className={styles.addButtons}>
                <button
                  className={styles.primaryBtn}
                  onClick={() => addMut.mutate('Watching')}
                  disabled={addMut.isPending}
                >
                  + Add to Library
                </button>
                <button
                  className={styles.secondaryBtn}
                  onClick={() => addMut.mutate('PlanToWatch')}
                  disabled={addMut.isPending}
                >
                  {getPlanToLabel(item.mediaTypeName)}
                </button>
              </div>
            )}
          </div>
        </div>
      </div>
        </div>{/* backdropContent */}
      </div>{/* backdropSection */}

      {/* Children (seasons, episodes, tracks, etc.) — sorted by number, then filename */}
      {children.length > 0 && (() => {
        const sortedChildren = [...children].sort((a, b) => {
          // Both have a number → numeric ascending
          if (a.number != null && b.number != null) return a.number - b.number
          // Only one has a number → numbered item comes first
          if (a.number != null) return -1
          if (b.number != null) return 1
          // Neither has a number → natural sort on name (handles "E01" < "E02" < "E10")
          return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' })
        })
        return (
        <section className={styles.children}>
          <h2 className={styles.childrenTitle}>
            {item.mediaTypeName === 'tv' ? 'Seasons' : 'Items'} ({sortedChildren.length})
          </h2>
          <div className={styles.childGrid}>
            {sortedChildren.map(child => (
              <Link key={child.id} to={`/media/${child.id}`} className={styles.childCard}>
                {child.posterUrl
                  ? <img
                      className={styles.childPoster}
                      src={child.posterUrl}
                      alt={child.name}
                      onError={e => {
                        const img = e.currentTarget
                        img.style.display = 'none'
                        const ph = img.nextElementSibling as HTMLElement | null
                        if (ph) ph.style.display = 'flex'
                      }}
                    />
                  : null}
                <div
                  className={styles.childPosterPlaceholder}
                  style={{ display: child.posterUrl ? 'none' : 'flex' }}
                >
                  {child.number ?? child.name.charAt(0)}
                </div>
                <div className={styles.childName}>{child.name}</div>
                {child.year && <div className={styles.childYear}>{child.year}</div>}
              </Link>
            ))}
          </div>
        </section>
        )
      })()}
    </div>
  )
}
