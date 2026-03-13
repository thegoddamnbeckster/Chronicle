import { useState, useRef, useEffect } from 'react'
import { useParams, useNavigate, Link, useLocation } from 'react-router-dom'
import tmdbLogoFallback from '@/assets/tmdb-logo.svg'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getMedia, getMediaChildren, refreshMedia, reidentifyMedia, deleteMedia } from '@/api/media'
import { getLibrary, addToLibrary, updateLibraryEntry } from '@/api/library'
import type { LibraryStatus } from '@/types'
import styles from './MediaDetailPage.module.css'

const STATUS_OPTIONS: LibraryStatus[] = [
  'Watching', 'PlanToWatch', 'Completed', 'Dropped', 'OnHold', 'Rewatching',
]

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

  if (isLoading) return <div className={styles.page}><p className={styles.loading}>Loading…</p></div>
  if (error || !item) {
    return (
      <div className={styles.page}>
        <p className={styles.error}>Media not found.</p>
        <button className={styles.backBtn} onClick={() => navigate(-1)}>← Back</button>
      </div>
    )
  }

  return (
    <div className={styles.page}>
      <div className={styles.topNav}>
        <button className={styles.backBtn} onClick={() => navigate(-1)}>← Back</button>
        <Link
          to={item.parentId != null ? `/media/${item.parentId}` : `/library#media-${item.id}`}
          className={styles.upBtn}
        >
          {item.parentId != null ? '↑ Up' : '↑ Library'}
        </Link>
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

        <div className={styles.meta}>
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

          {/* TMDB metadata box — always shown so Refresh is available even without metadata */}
          {item && (
            <div className={styles.metadataBox}>
              <div className={styles.metadataBoxHeader}>
                <div className={styles.metadataBoxBrand}>
                  <img
                    src="https://www.themoviedb.org/assets/2/v4/logos/v2/blue_short-8e7b30f73a4020692ccca9c88bafe5dcb20f201ad3a6b4d0b6dcea5b0b95d9f3.svg"
                    alt="TMDB"
                    className={styles.tmdbLogo}
                    onError={(e) => { e.currentTarget.src = tmdbLogoFallback; }}
                  />
                </div>
                <div className={styles.metadataBoxActions}>
                  <button
                    className={styles.fixMatchBtn}
                    onClick={() => { setFixMatchOpen(v => !v); reidentifyMut.reset() }}
                    title="Manually specify the correct TMDB match"
                  >
                    ✎ Fix Match
                  </button>
                  <button
                    className={styles.refreshBtn}
                    onClick={() => refreshMut.mutate()}
                    disabled={refreshMut.isPending}
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
                {item.tmdbMeta?.rating != null && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Rating</span>
                    <span className={styles.tmdbValue}>
                      {item.tmdbMeta.rating.toFixed(1)}&thinsp;/&thinsp;10
                    </span>
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
                {tmdbIds.length > 0 && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>ID</span>
                    <div className={styles.externalIds}>
                      {tmdbIds.map(eid => (
                        <span key={eid.externalId} className={styles.externalIdChip}>
                          <span className={styles.externalIdValue}>{eid.externalId}</span>
                        </span>
                      ))}
                    </div>
                  </div>
                )}

                {/* Image links — TMDB URLs only, no downloading */}
                {(item.tmdbMeta?.posterUrl || item.tmdbMeta?.backdropUrl) && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Images</span>
                    <div className={styles.tmdbImageLinks}>
                      {item.tmdbMeta?.posterUrl && (
                        <a
                          href={item.tmdbMeta.posterUrl}
                          target="_blank"
                          rel="noreferrer"
                          className={styles.tmdbImageLink}
                        >
                          Poster ↗
                        </a>
                      )}
                      {item.tmdbMeta?.backdropUrl && (
                        <a
                          href={item.tmdbMeta.backdropUrl}
                          target="_blank"
                          rel="noreferrer"
                          className={styles.tmdbImageLink}
                        >
                          Backdrop ↗
                        </a>
                      )}
                    </div>
                  </div>
                )}
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

          {/* File Scanner box */}
          {item.fileScannerMeta &&
            (item.fileScannerMeta.filePath ||
              item.fileScannerMeta.localPosterPath ||
              item.fileScannerMeta.nfoPosterUrl) && (
            <div className={styles.scannerBox}>
              <div className={styles.scannerHeader}>File Scanner</div>
              <div className={styles.tmdbGrid}>
                {item.fileScannerMeta.filePath && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>File</span>
                    <span className={styles.scannerPath}>{item.fileScannerMeta.filePath}</span>
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
                    <option key={s} value={s}>{s}</option>
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
                  Plan to Watch
                </button>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* Children (seasons, episodes, etc.) */}
      {children.length > 0 && (
        <section className={styles.children}>
          <h2 className={styles.childrenTitle}>
            {item.mediaTypeName === 'tv' ? 'Seasons' : 'Items'} ({children.length})
          </h2>
          <div className={styles.childGrid}>
            {children.map(child => (
              <Link key={child.id} to={`/media/${child.id}`} className={styles.childCard}>
                {child.posterUrl
                  ? <img className={styles.childPoster} src={child.posterUrl} alt={child.name} />
                  : <div className={styles.childPosterPlaceholder}>
                      {child.number ?? child.name.charAt(0)}
                    </div>
                }
                <div className={styles.childName}>{child.name}</div>
                {child.year && <div className={styles.childYear}>{child.year}</div>}
              </Link>
            ))}
          </div>
        </section>
      )}
    </div>
  )
}
