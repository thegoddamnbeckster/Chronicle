import { useState, useEffect, useRef } from 'react'
import { useParams, useNavigate, Link, useLocation } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getMedia, getMediaChildren, refreshMedia, deleteMedia } from '@/api/media'
import { getLibrary, addToLibrary, updateLibraryEntry } from '@/api/library'
import { listPlugins } from '@/api/plugins'
import type { LibraryStatus } from '@/types'
import { PluginMetadataBox } from '@/components/PluginMetadataBox'
import { extractImages, type ImageEntry } from '@/utils/imageExtractor'
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

/**
 * Returns the plural label for children of an item based on the parent's
 * media type and how deep in the hierarchy the parent is.
 *   TV  level 0 → "Seasons",  level 1 → "Episodes"
 *   music level 0 → "Albums", level 1 → "Tracks"
 *   anything else → "Items"
 */
function getChildrenLabel(parentMediaType: string, ancestorCount: number): string {
  const t = parentMediaType.toLowerCase()
  const childLevel = ancestorCount + 1
  if (t === 'tv' || t === 'tv shows') {
    if (childLevel === 1) return 'Seasons'
    if (childLevel === 2) return 'Episodes'
  }
  if (t === 'music') {
    if (childLevel === 1) return 'Albums'
    if (childLevel === 2) return 'Tracks'
  }
  return 'Items'
}

const LIGHTBOX_SKIP = new Set(['title', 'externalid', 'source', 'totalresults', 'total_results'])

export default function MediaDetailPage() {
  const { id } = useParams<{ id: string }>()
  const mediaId = Number(id)
  const navigate = useNavigate()
  const location = useLocation()
  const qc = useQueryClient()

  const navState = (location.state as { listIds?: number[]; listLabel?: string } | null) ?? null

  // Persist navigation state so breadcrumb / up-button navigation (which carries no state) can restore it
  useEffect(() => {
    if (navState?.listIds?.length) {
      sessionStorage.setItem(`chronicle.listNav.${mediaId}`, JSON.stringify(navState))
    }
  }, [mediaId, navState])

  // Fall back to sessionStorage when arriving via breadcrumb or direct URL (no location.state)
  const effectiveNavState = (() => {
    if (navState?.listIds?.length) return navState
    try {
      const stored = sessionStorage.getItem(`chronicle.listNav.${mediaId}`)
      return stored ? JSON.parse(stored) as { listIds: number[]; listLabel?: string } : null
    } catch { return null }
  })()

  const listIds = effectiveNavState?.listIds ?? []
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
      qc.invalidateQueries({ queryKey: ['media', mediaId, 'children'] })
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

  // ── Page-level unified lightbox ──────────────────────────────────────────
  // useState/useRef/useEffect must be before early returns (Rules of Hooks)
  const [lightboxIdx, setLightboxIdx] = useState<number | null>(null)
  const allImagesLenRef = useRef(0)

  useEffect(() => {
    if (lightboxIdx === null) return
    const len = allImagesLenRef.current
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setLightboxIdx(null)
      if (e.key === 'ArrowRight') setLightboxIdx(i => (i !== null && i < len - 1 ? i + 1 : i))
      if (e.key === 'ArrowLeft') setLightboxIdx(i => (i !== null && i > 0 ? i - 1 : i))
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [lightboxIdx])

  if (isLoading) return <div className={styles.page}><p className={styles.loading}>Loading…</p></div>
  if (error || !item) {
    return (
      <div className={styles.page}>
        <p className={styles.error}>Media not found.</p>
        <button className={styles.backBtn} onClick={() => navigate(-1)}>← Back</button>
      </div>
    )
  }

  // Compute full ordered image list: poster first, then each plugin's images in order
  const allImages: ImageEntry[] = [
    ...(item.posterUrl ? [{ url: item.posterUrl, label: 'Poster' }] : []),
    ...Object.values(item.pluginMetadata ?? {}).flatMap(meta =>
      extractImages(meta as Record<string, unknown>, LIGHTBOX_SKIP)
    ),
  ]
  allImagesLenRef.current = allImages.length

  // Per-plugin start offsets so PluginMetadataBox can pass the correct global index
  const pluginImageOffsets = new Map<string, number>()
  let imgOffset = item.posterUrl ? 1 : 0
  for (const [pluginId, meta] of Object.entries(item.pluginMetadata ?? {})) {
    pluginImageOffsets.set(pluginId, imgOffset)
    imgOffset += extractImages(meta as Record<string, unknown>, LIGHTBOX_SKIP).length
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
          <Link to={`/library#media-${mediaId}`} className={styles.upBtn}>↑ Library</Link>
        )}
        {listIds.length > 0 && (
          <div className={styles.listNav}>
            {prevId != null ? (
              <Link to={`/media/${prevId}`} state={effectiveNavState} className={styles.navBtn}>‹ Prev</Link>
            ) : (
              <span className={`${styles.navBtn} ${styles.navBtnDisabled}`}>‹ Prev</span>
            )}
            <span className={styles.navPos}>
              {effectiveNavState?.listLabel && <span className={styles.navLabel}>{effectiveNavState.listLabel} · </span>}
              {currentIndex + 1} / {listIds.length}
            </span>
            {nextId != null ? (
              <Link to={`/media/${nextId}`} state={effectiveNavState} className={styles.navBtn}>Next ›</Link>
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
                className={`${styles.poster} ${styles.posterClickable}`}
                src={item.posterUrl}
                alt={item.name}
                onClick={() => setLightboxIdx(0)}
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
            {item.runtimeMinutes != null && item.runtimeMinutes > 0 && (
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

          {/* Per-plugin metadata boxes — one box per plugin that has data OR has been attempted.
              This ensures Fix Match is always available, even for NotFound / failed items. */}
          {(() => {
            const pluginIds = new Set([
              ...Object.keys(item.pluginMetadata ?? {}),
              ...Object.keys(item.enrichmentStatuses ?? {}),
            ])
            return Array.from(pluginIds).map(pluginId => {
              const plugin = plugins.find(p => p.pluginId === pluginId)
              // Skip plugins that don't support this item's media type.
              // This prevents e.g. a TMDB "No match" box from appearing on Music items.
              // Use mediaTypeInternalName (canonical DB name like "tv") for comparison since
              // mediaTypeName is the display name ("TV Shows") which won't match plugin declarations.
              if (plugin?.supportedMediaTypes?.length) {
                const itemType = (item.mediaTypeInternalName ?? item.mediaTypeName).toLowerCase()
                const supported = plugin.supportedMediaTypes.some(t => t.toLowerCase() === itemType)
                if (!supported) return null
              }
              const metadata = item.pluginMetadata?.[pluginId]
              const enrichStatus = item.enrichmentStatuses?.[pluginId]
              return (
                <PluginMetadataBox
                  key={`${mediaId}-${pluginId}`}
                  mediaId={mediaId}
                  pluginId={pluginId}
                  pluginName={plugin?.name ?? pluginId}
                  iconUrl={plugin?.iconUrl}
                  fixMatchHint={plugin?.fixMatchHint}
                  metadata={metadata}
                  enrichmentStatus={enrichStatus}
                  externalIds={item.externalIds}
                  refreshLogs={item.refreshLogs}
                  onImageClick={(localIdx) => setLightboxIdx((pluginImageOffsets.get(pluginId) ?? 0) + localIdx)}
                  imageStartIndex={pluginImageOffsets.get(pluginId) ?? 0}
                />
              )
            })
          })()}

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
        const childrenLabel = getChildrenLabel(item.mediaTypeName, item.ancestors?.length ?? 0)
        const childIds = sortedChildren.map(c => c.id)
        return (
        <section className={styles.children}>
          <h2 className={styles.childrenTitle}>
            {childrenLabel} ({sortedChildren.length})
          </h2>
          <div className={styles.childGrid}>
            {sortedChildren.map(child => (
              <Link
                key={child.id}
                to={`/media/${child.id}`}
                state={{ listIds: childIds, listLabel: childrenLabel }}
                className={styles.childCard}
              >
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
                {(() => {
                  const enriched = child.enrichmentStatuses != null &&
                    Object.values(child.enrichmentStatuses).some(s => s === 'Completed')
                  return (
                    <div
                      className={styles.childPosterPlaceholder}
                      style={{ display: child.posterUrl ? 'none' : 'flex' }}
                    >
                      {enriched
                        ? <span className={styles.childNoArt}>No art</span>
                        : (child.number ?? child.name.charAt(0))}
                    </div>
                  )
                })()}
                <div className={styles.childName}>{child.name}</div>
                {child.year && <div className={styles.childYear}>{child.year}</div>}
              </Link>
            ))}
          </div>
        </section>
        )
      })()}

      {/* ── Page-level unified image lightbox ─────────────────────── */}
      {lightboxIdx !== null && (
        <div
          className={styles.lightboxOverlay}
          onClick={() => setLightboxIdx(null)}
          role="dialog"
          aria-modal="true"
          aria-label={allImages[lightboxIdx]?.label ?? 'Image'}
        >
          <button
            className={styles.lightboxClose}
            onClick={() => setLightboxIdx(null)}
            type="button"
            aria-label="Close"
          >
            ✕
          </button>
          {lightboxIdx > 0 && (
            <button
              className={`${styles.lightboxNav} ${styles.lightboxNavPrev}`}
              onClick={e => { e.stopPropagation(); setLightboxIdx(lightboxIdx - 1) }}
              type="button"
              aria-label="Previous image"
            >
              ‹
            </button>
          )}
          <img
            className={styles.lightboxImg}
            src={allImages[lightboxIdx]?.url}
            alt={allImages[lightboxIdx]?.label}
            onClick={e => e.stopPropagation()}
          />
          <div className={styles.lightboxCaption}>
            {allImages[lightboxIdx]?.label}
            {allImages.length > 1 && (
              <span className={styles.lightboxCounter}> {lightboxIdx + 1} / {allImages.length}</span>
            )}
          </div>
          {lightboxIdx < allImages.length - 1 && (
            <button
              className={`${styles.lightboxNav} ${styles.lightboxNavNext}`}
              onClick={e => { e.stopPropagation(); setLightboxIdx(lightboxIdx + 1) }}
              type="button"
              aria-label="Next image"
            >
              ›
            </button>
          )}
        </div>
      )}
    </div>
  )
}
