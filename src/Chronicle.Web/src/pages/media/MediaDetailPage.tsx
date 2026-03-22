import { useState } from 'react'
import { useParams, useNavigate, Link, useLocation } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getMedia, getMediaChildren, refreshMedia, deleteMedia } from '@/api/media'
import { getLibrary, addToLibrary, updateLibraryEntry } from '@/api/library'
import { listPlugins } from '@/api/plugins'
import type { LibraryStatus } from '@/types'
import { PluginMetadataBox } from '@/components/PluginMetadataBox'
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

  // Fetch installed plugins to get iconUrl + fixMatchHint for each plugin box
  const { data: plugins = [] } = useQuery({
    queryKey: ['plugins'],
    queryFn: listPlugins,
    staleTime: 5 * 60 * 1000,
  })

  const [deleteConfirm, setDeleteConfirm] = useState(false)

  const deleteMut = useMutation({
    mutationFn: () => deleteMedia(mediaId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['library'] })
      navigate('/library')
    },
  })

  if (isLoading) return <div className={styles.page}><p className={styles.loading}>Loading…</p></div>
  if (error || !item) {
    return (
      <div className={styles.page}>
        <p className={styles.error}>Media not found.</p>
        <button className={styles.backBtn} onClick={() => navigate(-1)}>← Back</button>
      </div>
    )
  }

  // Grab backdrop URL from any plugin that provides one
  const backdropUrl = item.pluginMetadata
    ? Object.values(item.pluginMetadata)
        .map(m => (m as Record<string, unknown>)?.backdropUrl)
        .find(u => typeof u === 'string' && u) as string | undefined
    : undefined
  const hasBackdrop = Boolean(backdropUrl)

  return (
    <div className={styles.page}>
      <div className={`${styles.backdropSection}${hasBackdrop ? ` ${styles.backdropActive}` : ''}`}>
        {hasBackdrop && (
          <div
            className={styles.backdropImg}
            style={{ backgroundImage: `url("${backdropUrl}")` }}
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

          {/* Global refresh strip — always shown so all media types can trigger enrichment */}
          <div className={styles.refreshStrip}>
            <button
              className={styles.refreshBtn}
              onClick={() => refreshMut.mutate()}
              disabled={refreshMut.isPending}
            >
              {refreshMut.isPending ? 'Refreshing all…' : '↻ Refresh All'}
            </button>
            {refreshMut.isError && (
              <span className={styles.refreshError}>
                {`Refresh failed: ${(refreshMut.error as Error).message}`}
              </span>
            )}
          </div>

          {/* Per-plugin metadata boxes — one box per plugin that has data for this item */}
          {item.pluginMetadata && Object.entries(item.pluginMetadata).map(([pluginId, metadata]) => {
            const plugin = plugins.find(p => p.pluginId === pluginId)
            return (
              <PluginMetadataBox
                key={pluginId}
                mediaId={mediaId}
                pluginId={pluginId}
                pluginName={plugin?.name ?? pluginId}
                iconUrl={plugin?.iconUrl}
                fixMatchHint={plugin?.fixMatchHint}
                metadata={metadata}
                externalIds={item.externalIds}
                refreshLogs={item.refreshLogs}
                hierarchyLevel={item.hierarchyLevel}
              />
            )
          })}

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
