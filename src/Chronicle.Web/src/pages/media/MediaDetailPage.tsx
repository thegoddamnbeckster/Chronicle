import React, { useState, useEffect, useRef } from 'react'
import { createPortal } from 'react-dom'
import { useParams, useNavigate, Link, useLocation } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getMedia, getMediaChildren, refreshMedia, deleteMedia, changeMediaType, unparentFromCollection, reparentToCollection, getNfoDetail, getCollections, clearAllMediaOverrides, setMediaOverride, clearMediaOverride, resetOverridesForSubtree } from '@/api/media'
import { getMediaTypes } from '@/api/media'
import { getLibrary, addToLibrary, updateLibraryEntry } from '@/api/library'
import { listPlugins } from '@/api/plugins'
import { getPluginDisplayOrder } from '@/api/settings'
import { getMyPreferences, updateMyPreferences } from '@/api/users'
import { useAuth } from '@/hooks/useAuth'
import type { LibraryStatus } from '@/types'
import { PluginMetadataBox } from '@/components/PluginMetadataBox'
import CollectionMetadataBox from '@/components/CollectionMetadataBox'
import { AdditionalImagesCard } from '@/components/AdditionalImagesCard'
import { SlotGalleryModal } from '@/components/SlotGalleryModal'
import { ManualImageUrlModal } from '@/components/ManualImageUrlModal'
import { ImageSlotControls } from '@/components/ImageSlotControls'
import { extractImages, buildSlotLookup, buildSlotImages, SLOT_INFO, type ImageEntry, type CanonicalSlot, type SlottedImageEntry } from '@/utils/imageExtractor'
import styles from './MediaDetailPage.module.css'
import { IconHdd } from '@/components/FileStatusIcons'
import { PosterImage } from '@/components/PosterImage'
import { FanartImage } from '@/components/FanartImage'
import MergeModal, { type MergeItem } from '@/components/MergeModal'
import { unmergeItem } from '@/api/duplicates'

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
  if (t === 'movies') {
    if (childLevel === 1) return 'Movies'
  }
  return 'Items'
}

const LIGHTBOX_SKIP = new Set(['title', 'externalid', 'source', 'totalresults', 'total_results'])

// ── Plugin fold ──────────────────────────────────────────────────────────────

// Module-level cache so preferences are fetched once per page session (not per component mount)
let _foldsCache: Record<string, boolean> | null = null
let _foldsFetch: Promise<Record<string, boolean>> | null = null

function getFoldsCache(): Promise<Record<string, boolean>> {
  if (_foldsCache !== null) return Promise.resolve(_foldsCache)
  if (_foldsFetch) return _foldsFetch
  _foldsFetch = getMyPreferences().then(p => {
    _foldsCache = p.folds ?? {}
    return _foldsCache
  }).catch(() => {
    _foldsCache = {}
    return _foldsCache
  })
  return _foldsFetch
}

function useFold(key: string, defaultOpen: boolean) {
  const [isOpen, setIsOpen] = useState(defaultOpen)
  const [loaded, setLoaded] = useState(false)

  useEffect(() => {
    let cancelled = false
    getFoldsCache().then(folds => {
      if (cancelled) return
      if (key in folds) setIsOpen(folds[key])
      setLoaded(true)
    })
    return () => { cancelled = true }
  }, [key])

  function toggle() {
    const next = !isOpen
    setIsOpen(next)
    if (_foldsCache) _foldsCache[key] = next
    updateMyPreferences({ folds: { [key]: next } }).catch(() => {})
  }

  return { isOpen, toggle, loaded }
}

interface PluginFoldProps {
  foldKey: string
  label: string
  iconUrl?: string | null
  defaultOpen?: boolean
  children: React.ReactNode
}

function PluginFold({ foldKey, label, iconUrl, defaultOpen = true, children }: PluginFoldProps) {
  const { isOpen, toggle } = useFold(foldKey, defaultOpen)

  return (
    <div className={styles.pluginFold}>
      <button className={styles.pluginFoldHeader} onClick={toggle} aria-expanded={isOpen}>
        {iconUrl && <img src={iconUrl} alt="" className={styles.pluginFoldIcon} onError={e => { e.currentTarget.style.display = 'none' }} />}
        <span className={styles.pluginFoldLabel}>{label}</span>
        <span className={`${styles.pluginFoldChevron} ${isOpen ? styles.pluginFoldChevronOpen : ''}`}>
          ›
        </span>
      </button>
      {isOpen && <div className={styles.pluginFoldBody}>{children}</div>}
    </div>
  )
}

export default function MediaDetailPage() {
  const { id } = useParams<{ id: string }>()
  const mediaId = Number(id)
  const navigate = useNavigate()
  const location = useLocation()
  const qc = useQueryClient()
  const { user } = useAuth()
  const isAdmin = user?.isAdmin ?? false

  const navState = (location.state as { listIds?: number[]; listLabel?: string } | null) ?? null

  // Persist navigation state so breadcrumb / up-button navigation (which carries no state) can restore it.
  // Wrapped in try/catch: large libraries can exceed the sessionStorage quota and would otherwise crash.
  useEffect(() => {
    if (navState?.listIds?.length) {
      try {
        sessionStorage.setItem(`chronicle.listNav.${mediaId}`, JSON.stringify(navState))
      } catch {
        // Quota exceeded — silently skip. Prev/Next nav won't persist across a hard refresh,
        // but navigation within the session (where location.state is in memory) still works.
      }
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

  const { data: nfoDetail } = useQuery({
    queryKey: ['media', mediaId, 'nfo'],
    queryFn: () => getNfoDetail(mediaId),
    enabled: !isNaN(mediaId) && !!item?.fileScannerMeta?.nfoPath,
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

  // React Router reuses this component instance across navigations — reset stale
  // mutation state so the Refresh All button isn't stuck as "Refreshing all…"
  // on a newly-loaded item when the previous item's mutation was still in-flight.
  const { reset: resetRefreshMut } = refreshMut
  useEffect(() => { resetRefreshMut() }, [mediaId, resetRefreshMut])

  // Fetch installed plugins to get iconUrl + fixMatchHint for each plugin box
  const { data: plugins = [] } = useQuery({
    queryKey: ['plugins'],
    queryFn: listPlugins,
    staleTime: 5 * 60 * 1000,
  })

  // Fetch the saved plugin display order (controls which box appears first/last)
  const { data: pluginDisplayOrder = {} } = useQuery({
    queryKey: ['plugin-display-order'],
    queryFn: getPluginDisplayOrder,
    staleTime: 60 * 1000,
  })

  const [deleteConfirm, setDeleteConfirm] = useState(false)

  const deleteMut = useMutation({
    mutationFn: () => deleteMedia(mediaId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['library'] })
      navigate('/library')
    },
  })

  const unparentMut = useMutation({
    mutationFn: () => unparentFromCollection(mediaId),
    onSuccess: (updated) => {
      qc.setQueryData(['media', mediaId], updated)
      qc.invalidateQueries({ queryKey: ['library'] })
      // Refetch every collection grid (this item's old parent, and this item's own page
      // in compact mode) so the removed item disappears immediately instead of only after
      // a manual reload — matches ['collection', <id>] for whichever collection is mounted.
      qc.invalidateQueries({ queryKey: ['collection'] })
    },
  })

  // ── Add to Collection / Remove from Collection (this item AS the collection root) ──
  // Lives here (not inside CollectionMetadataBox) because a brand-new, still-empty
  // collection has no children yet, and GetCollection 404s until it has at least one --
  // these controls have to work before that's true, so they're driven by getMediaChildren
  // (already fetched above as `children`) rather than the collection endpoint.
  const [addToCollectionOpen, setAddToCollectionOpen] = useState(false)
  const [addToCollectionQuery, setAddToCollectionQuery] = useState('')
  const [removeFromCollectionOpen, setRemoveFromCollectionOpen] = useState(false)

  const { data: addToCollectionResults = [] } = useQuery({
    queryKey: ['addToCollectionSearch', addToCollectionQuery],
    queryFn: async () => {
      if (!addToCollectionQuery.trim()) return []
      const { searchMedia } = await import('@/api/media')
      return searchMedia(addToCollectionQuery)
    },
    enabled: addToCollectionQuery.trim().length >= 2,
  })

  const reparentMut = useMutation({
    mutationFn: (movieId: number) => reparentToCollection(movieId, mediaId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['media', mediaId, 'children'] })
      qc.invalidateQueries({ queryKey: ['collection', mediaId] })
      qc.invalidateQueries({ queryKey: ['library'] })
      setAddToCollectionOpen(false)
      setAddToCollectionQuery('')
    },
  })

  // ── Join an Existing Collection (this item is a standalone movie, not yet a collection) ──
  // The inverse of the block above: here `mediaId` (this page's item) is the thing being
  // moved, and the user picks the destination collection. Deliberately a SEPARATE control
  // from "Add to Collection" rather than making that one bidirectional based on context --
  // reparentToCollection(id, collectionId) always means "move id under collectionId", so
  // the two flows just need to pass mediaId in different argument positions. Confirmed via
  // chronicle-20260802.log: using the OTHER control from a childless movie's own page passed
  // the picked collection as `id` and this movie as `collectionId`, reparenting the real
  // collection AS A CHILD of the movie instead of the movie into the collection -- exactly
  // backwards. That control is now gated on children.length > 0 (see below) so it can't be
  // misused this way again; this one is gated on children.length === 0 and does the reparent
  // in the correct direction from the start.
  const [joinCollectionOpen, setJoinCollectionOpen] = useState(false)
  const [joinCollectionQuery, setJoinCollectionQuery] = useState('')

  const { data: allCollections = [] } = useQuery({
    queryKey: ['collections', 'all'],
    queryFn: getCollections,
    enabled: joinCollectionOpen,
  })

  const joinCollectionMut = useMutation({
    mutationFn: (collectionId: number) => reparentToCollection(mediaId, collectionId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['media', mediaId] })
      qc.invalidateQueries({ queryKey: ['media', mediaId, 'children'] })
      qc.invalidateQueries({ queryKey: ['library'] })
      qc.invalidateQueries({ queryKey: ['collections'] })
      setJoinCollectionOpen(false)
      setJoinCollectionQuery('')
    },
  })

  const removeChildMut = useMutation({
    mutationFn: (childId: number) => unparentFromCollection(childId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['media', mediaId, 'children'] })
      qc.invalidateQueries({ queryKey: ['collection', mediaId] })
      qc.invalidateQueries({ queryKey: ['library'] })
    },
  })

  // ── Merge with… ──────────────────────────────────────────────────────────
  const [mergeSearchOpen, setMergeSearchOpen] = useState(false)
  const [mergeSearchQuery, setMergeSearchQuery] = useState('')
  const [mergeTarget, setMergeTarget] = useState<MergeItem | null>(null)

  const { data: mergeSearchResults = [] } = useQuery({
    queryKey: ['mergeSearch', mergeSearchQuery],
    queryFn: async () => {
      if (!mergeSearchQuery.trim()) return []
      const { searchMedia } = await import('@/api/media')
      return searchMedia(mergeSearchQuery)
    },
    enabled: mergeSearchQuery.trim().length >= 2,
  })

  const unmergeMut = useMutation({
    mutationFn: ({ mergeId }: { mergeId: number }) => unmergeItem(Number(id), mergeId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['media', mediaId] })
      qc.invalidateQueries({ queryKey: ['library'] })
    },
  })

  // ── Unmerge panel ────────────────────────────────────────────────────────
  const [unmergeOpen, setUnmergeOpen] = useState(false)
  const [unmergeAllPending, setUnmergeAllPending] = useState(false)

  const handleUnmergeAll = async () => {
    if (!item?.mergeHistory?.length) return
    if (!window.confirm(`Unmerge all ${item.mergeHistory.length} merged items? Each will be recreated as a stub.`)) return
    setUnmergeAllPending(true)
    try {
      for (const merge of item.mergeHistory) {
        await unmergeItem(mediaId, merge.mergeId)
      }
      await qc.invalidateQueries({ queryKey: ['media', mediaId] })
      await qc.invalidateQueries({ queryKey: ['library'] })
      setUnmergeOpen(false)
    } finally {
      setUnmergeAllPending(false)
    }
  }

  // ── Change Type ──────────────────────────────────────────────────────────
  const [changeTypeOpen, setChangeTypeOpen] = useState(false)
  const [changeTypeError, setChangeTypeError] = useState<string | null>(null)
  // After a type change the item moves to a different section of the library.
  // We capture the hash of an adjacent library entry *before* the mutation so
  // the "↑ Library" link can scroll to a nearby item rather than jumping to the
  // top of the page (which loses the user's place entirely).
  const [postChangeAnchor, setPostChangeAnchor] = useState<string | null>(null)

  // Always enabled -- isFlatCollectionType (below) depends on this to gate the
  // Add/Remove-to-Collection buttons, which must be visible on first page load,
  // not just after the user has opened the Change Type panel at least once.
  const { data: mediaTypes = [] } = useQuery({
    queryKey: ['media-types'],
    queryFn: getMediaTypes,
    staleTime: 5 * 60 * 1000,
  })

  const changeTypeMut = useMutation({
    mutationFn: (targetTypeId: number) => changeMediaType(mediaId, targetTypeId),
    onMutate: () => {
      // Snapshot the adjacent item while the library list is still fresh.
      const idx = library.findIndex(e => e.mediaItem.id === mediaId)
      if (idx !== -1) {
        const adjacent = library[idx - 1] ?? library[idx + 1]
        setPostChangeAnchor(adjacent ? `media-${adjacent.mediaItem.id}` : null)
      } else {
        setPostChangeAnchor(null)
      }
    },
    onSuccess: () => {
      setChangeTypeOpen(false)
      setChangeTypeError(null)
      qc.invalidateQueries({ queryKey: ['media', mediaId] })
      qc.invalidateQueries({ queryKey: ['library'] })
    },
    onError: (err: unknown) => {
      setChangeTypeError(err instanceof Error ? err.message : String(err))
    },
  })

  // ── Page-level unified lightbox ──────────────────────────────────────────
  // useState/useRef/useEffect must be before early returns (Rules of Hooks)
  const [lightboxIdx, setLightboxIdx] = useState<number | null>(null)
  const allImagesLenRef = useRef(0)

  // ── Additional Images: type-scoped gallery modal + promote/reset mutations ──
  const [gallerySlot, setGallerySlot] = useState<{
    slot: CanonicalSlot; slotLabel: string; images: SlottedImageEntry[]; startIndex: number
  } | null>(null)

  // Which slot has a set/clear request in flight, so only that chip shows a busy state.
  const [pendingSlot, setPendingSlot] = useState<CanonicalSlot | null>(null)

  // ── Manual "add image from URL" modal (for items no provider found any candidates for) ──
  const [manualImageOpen, setManualImageOpen] = useState(false)

  // Shared by both full-size viewers (the type-scoped gallery and the page-level lightbox).
  // The gallery deliberately stays OPEN after assigning, so several slots can be given the
  // same image in one visit and a mistaken pin can be undone on the spot.
  const overrideSetMut = useMutation({
    mutationFn: (p: { slot: CanonicalSlot; url: string; pluginId?: string; sourceType?: string }) => {
      setPendingSlot(p.slot)
      return setMediaOverride(mediaId, p.slot, p.url, p.pluginId, p.sourceType)
    },
    onSuccess: (updated) => {
      qc.setQueryData(['media', mediaId], updated)
      // The Library page's card grid is a separately cached query -- without this, a pinned
      // poster/backdrop/etc. shows correctly here but keeps showing the old image on Library
      // until something else happens to invalidate that cache.
      qc.invalidateQueries({ queryKey: ['library'] })
    },
    onSettled: () => setPendingSlot(null),
  })

  const overrideClearMut = useMutation({
    mutationFn: (slot: CanonicalSlot) => {
      setPendingSlot(slot)
      return clearMediaOverride(mediaId, slot)
    },
    onSuccess: (updated) => {
      qc.setQueryData(['media', mediaId], updated)
      qc.invalidateQueries({ queryKey: ['library'] })
    },
    onSettled: () => setPendingSlot(null),
  })

  const clearAllOverridesMut = useMutation({
    mutationFn: () => clearAllMediaOverrides(mediaId),
    onSuccess: (updated) => {
      qc.setQueryData(['media', mediaId], updated)
      qc.invalidateQueries({ queryKey: ['library'] })
    },
  })

  // Collection/show/artist-level reset. Runs as a background job (the subtree can be large),
  // so unlike the per-item reset there's no updated item to write straight back into the cache.
  const resetSubtreeMut = useMutation({
    mutationFn: () => resetOverridesForSubtree(mediaId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['media', mediaId] }) },
  })

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

  // url -> {slot, pluginId} so the page-level lightbox (which mixes every image type) can
  // record provenance when pinning. Assignment itself is never restricted to the looked-up
  // slot — ImageSlotControls offers all of them.
  const slotLookup = buildSlotLookup(item.pluginMetadata)

  // Opens the type-scoped gallery directly from an on-page logo/banner/thumb/disc/character
  // image (which, unlike the poster, has no "Additional Images" card row to click through) --
  // same mechanism, just entered from the rendered image itself instead of a thumbnail.
  function openSlotGallery(slot: CanonicalSlot) {
    const images = buildSlotImages(item!, slot)
    if (images.length === 0) return
    setGallerySlot({ slot, slotLabel: SLOT_INFO[slot].label, images, startIndex: 0 })
  }

  // Per-plugin start offsets so PluginMetadataBox can pass the correct global index
  const pluginImageOffsets = new Map<string, number>()
  let imgOffset = item.posterUrl ? 1 : 0
  for (const [pluginId, meta] of Object.entries(item.pluginMetadata ?? {})) {
    pluginImageOffsets.set(pluginId, imgOffset)
    imgOffset += extractImages(meta as Record<string, unknown>, LIGHTBOX_SKIP).length
  }

  // Logo/banner/thumb/disc/character art: read from resolvedMetadata first (the
  // override-aware, priority-resolved value — this is what makes promoting one of these
  // slots in the Additional Images card actually change what renders here), falling back
  // to a raw per-plugin scan for items that haven't been through MetadataResolution yet.
  // Previously these bypassed resolvedMetadata entirely and read the Fanart.tv blob
  // directly, which meant an override on these fields would have no visible effect.
  const rawArtScan = (key: string): string | null =>
    item.pluginMetadata
      ? ((Object.values(item.pluginMetadata)
          .map(m => (m as Record<string, unknown>)?.[key])
          .find(u => typeof u === 'string' && u) as string | undefined) ?? null)
      : null
  const fanartLogo      = item.resolvedMetadata?.logoUrl        ?? rawArtScan('logoUrl')
  const fanartBanner    = item.resolvedMetadata?.bannerUrl      ?? rawArtScan('bannerUrl')
  const fanartThumb     = item.resolvedMetadata?.thumbUrl       ?? rawArtScan('thumbUrl')
  const fanartDisc      = item.resolvedMetadata?.discUrl        ?? rawArtScan('discUrl')
  const fanartCharacter = item.resolvedMetadata?.characterArtUrl ?? rawArtScan('characterArtUrl')

  // Backdrop: resolved metadata respects the assignment-priority config; fall back to
  // raw per-plugin scan for items that haven't been through MetadataResolution yet.
  const backdropUrl = item.resolvedMetadata?.backdropUrl
    ?? (item.pluginMetadata
      ? (Object.values(item.pluginMetadata)
          .map(m => (m as Record<string, unknown>)?.backdropUrl)
          .find(u => typeof u === 'string' && u) as string | undefined)
      : undefined)
  const hasBackdrop = Boolean(backdropUrl)

  // Collections apply to flat (non-hierarchical) media types — a type with a natural multi-level
  // hierarchy (TV Show/Season/Episode, Music Artist/Album/Track, or "anime" itself) already uses
  // ParentId/HierarchyLevel for its own structure, so grouping its Level-0 items into an ad-hoc
  // "collection" would conflict with that. Standalone anime films live on the flat anime_movies
  // type instead. Mirrors the backend's HierarchyLevels == 1 check.
  const currentMediaType = mediaTypes.find(t => t.id === item.mediaTypeId)
  const isFlatCollectionType = currentMediaType?.hierarchyLevels === 1

  // children.length alone can't tell a brand-new, still-empty collection apart from a genuine
  // standalone movie -- both are HierarchyLevel 0 / no parent / no children. A collection tagged
  // with its TMDB collection ID (via the Add Collection page's "TMDB Collection URL" lookup) is
  // unambiguous even at zero children, so treat that as a known collection too. Without this, a
  // freshly created empty collection showed "Add to a Collection" (join another collection) --
  // the wrong direction -- instead of "Add to Collection" (add movies into this one).
  const isKnownCollection = children.length > 0
    || item.externalIds.some(e => e.externalId?.toLowerCase().startsWith('collection:'))

  const joinCollectionResults = allCollections
    .filter(c => c.mediaTypeId === item.mediaTypeId
      && c.id !== mediaId
      && c.name.toLowerCase().includes(joinCollectionQuery.trim().toLowerCase()))
    .slice(0, 8)

  // Extract narrators from cast entries across all plugin metadata.
  // Cast entries are {name, role} objects (see CastMemberDto) -- Hardcover tags
  // narrators with role "Narrator".
  const narrators: string[] = []
  const isAudiobookType = (item.mediaTypeInternalName ?? item.mediaTypeName).toLowerCase() === 'audiobooks'
  if (isAudiobookType) {
    for (const meta of Object.values(item.pluginMetadata ?? {})) {
      const cast = (meta as Record<string, unknown>)?.cast
      if (Array.isArray(cast)) {
        const found = cast
          .filter((c: unknown): c is { name: string; role?: string } =>
            typeof c === 'object' && c !== null && 'name' in c && (c as { role?: string }).role === 'Narrator')
          .map(c => c.name)
        if (found.length > 0) { narrators.push(...found); break }
      }
    }
  }

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
              <Link to={`/library#media-${item.ancestors![0].id}`} className={styles.breadcrumbLink}>Library</Link>
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
          <Link
            to={changeTypeMut.isSuccess
                  ? (postChangeAnchor ? `/library#${postChangeAnchor}` : '/library')
                  : `/library#media-${mediaId}`}
            className={styles.upBtn}
          >↑ Library</Link>
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

      {fanartBanner && (
        <FanartImage
          src={fanartBanner}
          wrapperClassName={styles.fanartBannerWrap}
          imgClassName={styles.fanartBanner}
          minHeight={60}
          onClick={() => openSlotGallery('banner_url')}
        />
      )}

      <div className={styles.hero}>
        <div className={styles.posterWrap}>
          <PosterImage
            posterUrl={item.posterUrl}
            name={item.name}
            imgClassName={styles.posterClickable}
            onClick={() => setLightboxIdx(0)}
          />
          {fanartCharacter && (
            <FanartImage
              src={fanartCharacter}
              wrapperClassName={styles.fanartCharacterWrap}
              imgClassName={styles.fanartCharacter}
              minHeight={120}
              onClick={() => openSlotGallery('character_art_url')}
            />
          )}
        </div>

        <div className={`${styles.meta}${hasBackdrop ? ` ${styles.metaBoxed}` : ''}`}>
          {fanartLogo && (
            <FanartImage
              src={fanartLogo}
              alt={item.name}
              wrapperClassName={styles.fanartLogoWrap}
              imgClassName={styles.fanartLogo}
              minHeight={80}
              onClick={() => openSlotGallery('logo_url')}
            />
          )}
          <h1 className={styles.title}>{item.name}</h1>
          {item.aliases && item.aliases.length > 0 && (
            <p className={styles.aliases}>Also known as: {item.aliases.join(', ')}</p>
          )}
          {narrators.length > 0 && (
            <p className={styles.narratorLine}>Narrated by {narrators.join(', ')}</p>
          )}

          <div className={styles.deleteArea}>
            {isAdmin && (
              <button
                className={styles.changeTypeBtn}
                onClick={() => setManualImageOpen(true)}
                title="Pin an image Chronicle didn't find on its own — useful when no provider has any candidates for this item"
              >
                + Add Image URL
              </button>
            )}
            {isAdmin && item.parentId == null && (
              <button
                className={styles.changeTypeBtn}
                onClick={() => { setChangeTypeOpen(true); setChangeTypeError(null) }}
              >
                Change Type
              </button>
            )}
            {isAdmin && item.parentId != null && item.ancestors && item.ancestors.length > 0 && (
              <Link
                to={`/media/${item.ancestors[0].id}`}
                className={styles.changeTypeBtn}
                title="To change type, go to the collection root"
                style={{ textDecoration: 'none', textAlign: 'center' }}
              >
                Change Type (at root)
              </Link>
            )}
            {isAdmin && item.hierarchyLevel === 1 && item.parentId != null && isFlatCollectionType && (
              <button
                className={styles.changeTypeBtn}
                onClick={() => unparentMut.mutate()}
                disabled={unparentMut.isPending}
                title="Remove this item from its collection so you can change its type or manage it independently"
              >
                {unparentMut.isPending ? 'Removing…' : 'Remove from Collection'}
              </button>
            )}
            {/* Both actions below reparent the SEARCH RESULT to become a child of THIS page's
                item (see reparentMut: reparentToCollection(movieId, mediaId)) -- correct only
                when this page's item is already an established (or intentionally created)
                collection. Gated on isKnownCollection, not just hierarchyLevel/parentId,
                because a brand-new standalone movie is ALSO hierarchyLevel 0 / parentId null /
                isFlatCollectionType; showing this control there let a user "add" a real
                collection into a plain movie, inverting the hierarchy (the movie became the
                root, the collection its child) -- confirmed via chronicle-20260802.log around
                20:26:05 (POST /media/{collectionId}/reparent against a childless movie's page).
                A standalone item now gets its OWN correctly-directed control below ("Add to a
                Collection") instead of reusing this one. "Remove from Collection" (removing a
                current member) additionally requires children.length > 0 -- there's nothing to
                remove from a collection that has no members yet. */}
            {isAdmin && item.hierarchyLevel === 0 && item.parentId == null && isFlatCollectionType && isKnownCollection && (
              <>
                <button
                  className={styles.changeTypeBtn}
                  onClick={() => { setAddToCollectionOpen(o => !o); setRemoveFromCollectionOpen(false) }}
                  title="Add an existing standalone item of the same media type into this collection"
                >
                  {addToCollectionOpen ? 'Cancel' : 'Add to Collection'}
                </button>
                {children.length > 0 && (
                  <button
                    className={styles.changeTypeBtn}
                    onClick={() => { setRemoveFromCollectionOpen(o => !o); setAddToCollectionOpen(false) }}
                    title="Remove one of this collection's current items"
                  >
                    {removeFromCollectionOpen ? 'Cancel' : 'Remove from Collection'}
                  </button>
                )}
              </>
            )}
            {/* Inverse of the block above: this page's item is a standalone movie (not a known
                collection), so the action is "move ME under an existing collection" --
                joinCollectionMut reparents mediaId (this item) under the picked collectionId,
                the direction that was previously inverted (see comment above). */}
            {isAdmin && item.hierarchyLevel === 0 && item.parentId == null && isFlatCollectionType && !isKnownCollection && (
              <button
                className={styles.changeTypeBtn}
                onClick={() => setJoinCollectionOpen(o => !o)}
                title="Add this item into an existing collection"
              >
                {joinCollectionOpen ? 'Cancel' : 'Add to a Collection'}
              </button>
            )}
            {isAdmin && (
              <button
                className={styles.changeTypeBtn}
                onClick={() => setMergeSearchOpen(o => !o)}
              >
                {mergeSearchOpen ? 'Cancel Merge' : 'Merge with…'}
              </button>
            )}
            {isAdmin && item.mergeHistory && item.mergeHistory.length > 0 && (
              <button
                className={styles.changeTypeBtn}
                onClick={() => setUnmergeOpen(o => !o)}
              >
                {unmergeOpen ? 'Cancel' : `Unmerge… (${item.mergeHistory.length})`}
              </button>
            )}
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

          {/* Unmerge panel */}
          {unmergeOpen && item.mergeHistory && item.mergeHistory.length > 0 && (
            <div className={styles.unmergePanel}>
              <div className={styles.unmergePanelHeader}>
                <p className={styles.unmergePanelTitle}>Select a merge to undo:</p>
                <button
                  className={styles.unmergeAllBtn}
                  disabled={unmergeAllPending || unmergeMut.isPending}
                  onClick={handleUnmergeAll}
                >
                  {unmergeAllPending ? 'Unmerging…' : `Unmerge All (${item.mergeHistory.length})`}
                </button>
              </div>
              {item.mergeHistory.map(merge => (
                <div key={merge.mergeId} className={styles.unmergePanelRow}>
                  <span className={styles.unmergePanelName}>{merge.loserName}</span>
                  <span className={styles.unmergePanelDate}>
                    merged {new Date(merge.mergedAt).toLocaleDateString()}
                  </span>
                  <button
                    className={styles.unmergePanelBtn}
                    disabled={unmergeMut.isPending}
                    onClick={() => {
                      if (window.confirm(
                        `Unmerge "${merge.loserName}"? It will be recreated as a stub and queued for re-enrichment.`
                      )) {
                        unmergeMut.mutate({ mergeId: merge.mergeId })
                        setUnmergeOpen(false)
                      }
                    }}
                  >
                    Unmerge
                  </button>
                </div>
              ))}
            </div>
          )}

          {/* Change Type modal — rendered via portal so it escapes the backdrop's CSS variable overrides */}
          {changeTypeOpen && createPortal(
            <div className={styles.changeTypeOverlay} onClick={() => setChangeTypeOpen(false)}>
              <div className={styles.changeTypeModal} onClick={e => e.stopPropagation()}>
                <h3 className={styles.changeTypeTitle}>Change Media Type</h3>
                <p className={styles.changeTypeWarning}>
                  This will reset all metadata, enrichment status, and external IDs for this item.
                  This cannot be undone.
                </p>
                <div className={styles.changeTypeList}>
                  {mediaTypes
                    .filter(t => t.id !== item.mediaTypeId)
                    .map(t => (
                      <button
                        key={t.id}
                        className={styles.changeTypeOption}
                        onClick={() => {
                          setChangeTypeError(null)
                          changeTypeMut.mutate(t.id)
                        }}
                        disabled={changeTypeMut.isPending}
                      >
                        {t.displayName}
                      </button>
                    ))
                  }
                </div>
                {changeTypeError && (
                  <p className={styles.changeTypeError}>{changeTypeError}</p>
                )}
                <div className={styles.changeTypeActions}>
                  <button
                    className={styles.deleteConfirmCancel}
                    onClick={() => setChangeTypeOpen(false)}
                    disabled={changeTypeMut.isPending}
                  >
                    Cancel
                  </button>
                </div>
              </div>
            </div>,
            document.body
          )}

          {mergeSearchOpen && (
            <div className={styles.mergeSearch}>
              <input
                className={styles.mergeSearchInput}
                type="text"
                placeholder="Search for item to merge with…"
                value={mergeSearchQuery}
                onChange={e => setMergeSearchQuery(e.target.value)}
                autoFocus
              />
              {mergeSearchResults.length > 0 && (
                <div className={styles.mergeSearchResults}>
                  {mergeSearchResults.filter(r => r.id !== mediaId).slice(0, 8).map(result => (
                    <button
                      key={result.id}
                      className={styles.mergeSearchResult}
                      onClick={() => {
                        setMergeTarget({
                          id: result.id,
                          name: result.name,
                          posterUrl: result.posterUrl,
                          mediaTypeName: result.mediaTypeName,
                          year: result.year,
                          runtimeMinutes: result.runtimeMinutes,
                          overview: result.overview,
                          filePath: result.fileScannerMeta?.filePath ?? null,
                        })
                        setMergeSearchOpen(false)
                        setMergeSearchQuery('')
                      }}
                    >
                      {result.posterUrl && (
                        <img src={result.posterUrl} alt="" className={styles.mergeResultPoster} />
                      )}
                      <span className={styles.mergeResultText}>
                        <span className={styles.mergeResultName}>{result.name}{result.year ? ` (${result.year})` : ''}</span>
                        {result.mediaTypeName && <span className={styles.mergeResultType}>{result.mediaTypeName}</span>}
                      </span>
                    </button>
                  ))}
                </div>
              )}
            </div>
          )}

          {addToCollectionOpen && (
            <div className={styles.mergeSearch}>
              <input
                className={styles.mergeSearchInput}
                type="text"
                placeholder="Search standalone movies to add…"
                value={addToCollectionQuery}
                onChange={e => setAddToCollectionQuery(e.target.value)}
                autoFocus
              />
              {reparentMut.isError && (
                <p className={styles.changeTypeError}>
                  {reparentMut.error instanceof Error ? reparentMut.error.message : 'Failed to add to collection.'}
                </p>
              )}
              {addToCollectionResults.length > 0 && (
                <div className={styles.mergeSearchResults}>
                  {addToCollectionResults
                    .filter(r => r.id !== mediaId && r.parentId == null && r.mediaTypeId === item.mediaTypeId)
                    .slice(0, 8)
                    .map(result => (
                      <button
                        key={result.id}
                        className={styles.mergeSearchResult}
                        onClick={() => reparentMut.mutate(result.id)}
                        disabled={reparentMut.isPending}
                      >
                        {result.posterUrl && (
                          <img src={result.posterUrl} alt="" className={styles.mergeResultPoster} />
                        )}
                        <span className={styles.mergeResultText}>
                          <span className={styles.mergeResultName}>{result.name}{result.year ? ` (${result.year})` : ''}</span>
                        </span>
                      </button>
                    ))}
                </div>
              )}
            </div>
          )}

          {joinCollectionOpen && (
            <div className={styles.mergeSearch}>
              <input
                className={styles.mergeSearchInput}
                type="text"
                placeholder="Search collections to add this into…"
                value={joinCollectionQuery}
                onChange={e => setJoinCollectionQuery(e.target.value)}
                autoFocus
              />
              {joinCollectionMut.isError && (
                <p className={styles.changeTypeError}>
                  {joinCollectionMut.error instanceof Error ? joinCollectionMut.error.message : 'Failed to add to collection.'}
                </p>
              )}
              {joinCollectionResults.length > 0 && (
                <div className={styles.mergeSearchResults}>
                  {joinCollectionResults.map(result => (
                    <button
                      key={result.id}
                      className={styles.mergeSearchResult}
                      onClick={() => joinCollectionMut.mutate(result.id)}
                      disabled={joinCollectionMut.isPending}
                    >
                      {result.posterUrl && (
                        <img src={result.posterUrl} alt="" className={styles.mergeResultPoster} />
                      )}
                      <span className={styles.mergeResultText}>
                        <span className={styles.mergeResultName}>{result.name}</span>
                        <span className={styles.mergeResultType}>{result.itemCount} item{result.itemCount === 1 ? '' : 's'}</span>
                      </span>
                    </button>
                  ))}
                </div>
              )}
            </div>
          )}

          {removeFromCollectionOpen && (
            <div className={styles.removeFromCollectionPanel}>
              {children.length === 0 ? (
                <p className={styles.removeFromCollectionEmpty}>This collection has no movies yet.</p>
              ) : (
                children.map(child => (
                  <div key={child.id} className={styles.removeFromCollectionRow}>
                    {child.posterUrl && (
                      <img src={child.posterUrl} alt="" className={styles.mergeResultPoster} />
                    )}
                    <span className={styles.mergeResultText}>
                      <span className={styles.mergeResultName}>{child.name}{child.year ? ` (${child.year})` : ''}</span>
                    </span>
                    <button
                      className={styles.changeTypeBtn}
                      onClick={() => removeChildMut.mutate(child.id)}
                      disabled={removeChildMut.isPending}
                    >
                      {removeChildMut.isPending ? 'Removing…' : 'Remove'}
                    </button>
                  </div>
                ))
              )}
            </div>
          )}

          <div className={styles.metaRow}>
            {item.year && <span className={styles.chip}>{item.year}</span>}
            <span className={styles.chip}>{item.mediaTypeName}</span>
            {item.runtimeMinutes != null && item.runtimeMinutes > 0 && (
              <span className={styles.chip}>{item.runtimeMinutes} min</span>
            )}
            {item.hasPhysicalFile && (
              <div className={styles.fileIndicator}>
                <span className={styles.fileIcon} title="Has physical file on disk"><IconHdd /></span>
              </div>
            )}
          </div>

          {(item.overview || fanartThumb || fanartDisc) && (
            <div className={styles.descriptionRow}>
              {fanartThumb && (
                <FanartImage
                  src={fanartThumb}
                  wrapperClassName={styles.fanartThumbWrap}
                  imgClassName={styles.fanartThumb}
                  minHeight={150}
                  onClick={() => openSlotGallery('thumb_url')}
                />
              )}
              {item.overview && <p className={styles.overview}>{item.overview}</p>}
              {fanartDisc && (
                <FanartImage
                  src={fanartDisc}
                  wrapperClassName={styles.fanartDiscWrap}
                  imgClassName={styles.fanartDisc}
                  minHeight={110}
                  onClick={() => openSlotGallery('disc_url')}
                />
              )}
            </div>
          )}

          {/* Global refresh strip — always shown so all media types can trigger enrichment */}
          <div className={styles.refreshStrip}>
            <button
              className={styles.refreshBtn}
              onClick={() => refreshMut.mutate()}
              disabled={refreshMut.isPending}
            >
              {refreshMut.isPending ? 'Refreshing all…' : '↻ Refresh All'}
            </button>
            {item.overrides && Object.keys(item.overrides).length > 0 && (
              <button
                className={styles.refreshBtn}
                onClick={() => clearAllOverridesMut.mutate()}
                disabled={clearAllOverridesMut.isPending}
                title="Un-pin every manually-chosen image on this item, reverting all to the normal resolution"
              >
                {clearAllOverridesMut.isPending ? 'Resetting…' : '↺ Reset All Image Overrides'}
              </button>
            )}
            {/* Subtree reset — a collection/show/artist can hold pinned art on its members as
                well as on itself, and those aren't reachable from this page's own reset. */}
            {children.length > 0 && (
              <button
                className={styles.refreshBtn}
                onClick={() => resetSubtreeMut.mutate()}
                disabled={resetSubtreeMut.isPending}
                title="Un-pin every manually-chosen image on this item AND everything inside it, reverting all to the normal resolution"
              >
                {resetSubtreeMut.isPending ? 'Resetting…' : '↺ Reset Image Overrides (incl. contents)'}
              </button>
            )}
            {refreshMut.isError && (
              <span className={styles.refreshError}>
                {`Refresh failed: ${(refreshMut.error as Error).message}`}
              </span>
            )}
            {clearAllOverridesMut.isError && (
              <span className={styles.refreshError}>
                {`Reset failed: ${(clearAllOverridesMut.error as Error).message}`}
              </span>
            )}
            {resetSubtreeMut.isError && (
              <span className={styles.refreshError}>
                {`Reset failed: ${(resetSubtreeMut.error as Error).message}`}
              </span>
            )}
            {resetSubtreeMut.isSuccess && (
              <span className={styles.refreshNote}>
                Reset started — refresh the page in a moment to see the result.
              </span>
            )}
          </div>

          {/* Collection membership box — collections now work for any flat (non-hierarchical)
              media type, not just movies/fanedits/anime. compact=true on individual items: just
              show "Part of X" header, no card grid. On collection containers (Level 0) the full
              grid is shown. */}
          {isFlatCollectionType && (
            <CollectionMetadataBox mediaItemId={mediaId} compact={item.hierarchyLevel === 1} />
          )}

          {/* Per-plugin metadata boxes — one box per plugin that has data OR has been attempted.
              This ensures Fix Match is always available, even for NotFound / failed items.
              Boxes are sorted by the Display Order configured on the Metadata Assignment page. */}
          {(() => {
            const rawPluginIds = new Set([
              ...Object.keys(item.pluginMetadata ?? {}),
              ...Object.keys(item.enrichmentStatuses ?? {}),
            ])
            // Sort by the saved display order for this media type.
            // Plugins not in the order list appear at the end (stable insertion order).
            const mediaTypeKey = (item.mediaTypeInternalName ?? item.mediaTypeName ?? '').toLowerCase()
            const orderedIds   = pluginDisplayOrder[mediaTypeKey] ?? []
            const pluginIds    = [
              ...orderedIds.filter(id => rawPluginIds.has(id)),
              ...Array.from(rawPluginIds).filter(id => !orderedIds.includes(id)),
            ]
            return pluginIds.map(pluginId => {
              const plugin = plugins.find(p => p.pluginId === pluginId)
              // Skip a plugin that's been uninstalled (not in `plugins` at all) or disabled --
              // its historical enrichment/metadata rows persist on the item forever, but a
              // removed/disabled plugin should stop showing up anywhere, not linger as a dead
              // "No match" card on every item it was ever attempted against. Reported: a Trakt
              // card kept appearing after the user disabled it, expecting it gone entirely.
              if (!plugin || !plugin.isEnabled) return null
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
                <PluginFold
                  key={`${mediaId}-${pluginId}`}
                  foldKey={`media.${mediaId}.${pluginId}`}
                  label={plugin?.name ?? pluginId}
                  iconUrl={plugin?.iconUrl}
                  defaultOpen={!pluginId.includes('fanarttv')}
                >
                  <PluginMetadataBox
                    mediaId={mediaId}
                    pluginId={pluginId}
                    pluginName={plugin?.name ?? pluginId}
                    iconUrl={plugin?.iconUrl}
                    fixMatchHint={plugin?.fixMatchHint}
                    metadata={metadata}
                    enrichmentStatus={enrichStatus}
                    externalIds={item.externalIds ?? []}
                    refreshLogs={item.refreshLogs}
                    onImageClick={setLightboxIdx}
                    imageStartIndex={pluginImageOffsets.get(pluginId) ?? 0}
                  />
                </PluginFold>
              )
            })
          })()}

          {/* Additional Images — every image available across all plugins for this item,
              grouped by the artwork type its source plugin reported. Browse-only: clicking a
              thumbnail opens it full size, which is the only place artwork can be assigned. */}
          <AdditionalImagesCard
            item={item}
            onOpenGallery={(slot, slotLabel, images, startIndex) =>
              setGallerySlot({ slot, slotLabel, images, startIndex })
            }
          />

          {/* File Scanner box — show whenever the item came from the scanner, OR
              whenever hasPhysicalFile says a file is tracked on disk. hasPhysicalFile
              is the authoritative flag; fileScannerMeta can theoretically lag behind it
              (e.g. not yet loaded), so a physical file must never be hidden just
              because fileScannerMeta itself is sparse. */}
          {(item.hasPhysicalFile ||
            (item.fileScannerMeta &&
              (item.fileScannerMeta.filePath ||
                item.fileScannerMeta.localPosterPath ||
                item.fileScannerMeta.nfoPosterUrl ||
                item.fileScannerMeta.importedAt))) && (
            <div className={styles.scannerBox}>
              <div className={styles.scannerHeader}>File Scanner</div>
              <div className={styles.tmdbGrid}>
                {item.fileScannerMeta?.filePath && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>File</span>
                    <span className={styles.scannerPath}>{item.fileScannerMeta.filePath}</span>
                  </div>
                )}
                {!item.fileScannerMeta?.filePath && item.hasPhysicalFile && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>File</span>
                    <span className={styles.scannerPath} style={{ color: 'var(--text-muted)' }}>
                      Tracked on disk, but the file path hasn't loaded yet.
                    </span>
                  </div>
                )}
                {!item.fileScannerMeta?.filePath && !item.hasPhysicalFile && item.fileScannerMeta?.importedAt && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Imported</span>
                    <span className={styles.scannerPath}>
                      {new Date(item.fileScannerMeta.importedAt).toLocaleString()}
                    </span>
                  </div>
                )}
                {item.fileScannerMeta?.localPosterPath && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Poster</span>
                    <div className={styles.scannerPosterWrap}>
                      <img
                        src={`/api/v1/media/${item.id}/local-poster`}
                        alt="Local poster"
                        className={styles.scannerPosterThumb}
                      />
                      <span className={styles.scannerPath}>{item.fileScannerMeta.localPosterPath}</span>
                    </div>
                  </div>
                )}
                {item.fileScannerMeta?.nfoPosterUrl && (
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
                {nfoDetail?.plot && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Plot</span>
                    <span className={styles.tmdbValue}>{nfoDetail.plot}</span>
                  </div>
                )}
                {nfoDetail && nfoDetail.genres.length > 0 && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Genres</span>
                    <div className={styles.tmdbTags}>
                      {nfoDetail.genres.map(g => (
                        <span key={g} className={styles.tmdbTag}>{g}</span>
                      ))}
                    </div>
                  </div>
                )}
                {nfoDetail?.rating != null && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Rating</span>
                    <span className={styles.tmdbValue}>{nfoDetail.rating.toFixed(1)}</span>
                  </div>
                )}
                {(nfoDetail?.runtimeMinutes || nfoDetail?.mpaa || nfoDetail?.premiered) && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Details</span>
                    <span className={styles.tmdbValue}>
                      {[
                        nfoDetail?.runtimeMinutes ? `${nfoDetail.runtimeMinutes} min` : null,
                        nfoDetail?.mpaa,
                        nfoDetail?.premiered,
                      ].filter(Boolean).join(' · ')}
                    </span>
                  </div>
                )}
                {nfoDetail?.studio && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Studio</span>
                    <span className={styles.tmdbValue}>{nfoDetail.studio}</span>
                  </div>
                )}
                {nfoDetail?.director && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Director</span>
                    <span className={styles.tmdbValue}>{nfoDetail.director}</span>
                  </div>
                )}
                {nfoDetail && nfoDetail.writers.length > 0 && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Writers</span>
                    <span className={styles.tmdbValue}>{nfoDetail.writers.join(', ')}</span>
                  </div>
                )}
                {nfoDetail?.collectionName && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Collection</span>
                    <span className={styles.tmdbValue}>{nfoDetail.collectionName}</span>
                  </div>
                )}
                {nfoDetail && nfoDetail.actors.length > 0 && (
                  <div className={styles.tmdbRow}>
                    <span className={styles.tmdbLabel}>Cast</span>
                    <span className={styles.tmdbValue}>
                      {nfoDetail.actors
                        .map(a => a.role ? `${a.name} (${a.role})` : a.name)
                        .join(', ')}
                    </span>
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

      {/* Children (seasons, episodes, tracks, etc.) — sorted by number, then filename.
          Flat-type collections are sorted by year ascending (oldest first).
          For flat-type collections the CollectionMetadataBox already shows the children — skip. */}
      {children.length > 0 && (() => {
        const isMovieCollection = isFlatCollectionType && item.hierarchyLevel === 0

        if (isMovieCollection) return null

        const sortedChildren = [...children].sort((a, b) => {
          if (isMovieCollection) {
            // Oldest release first; null years sort to end
            const ya = a.year ?? 9999
            const yb = b.year ?? 9999
            return ya !== yb ? ya - yb : a.name.localeCompare(b.name)
          }
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
                {(() => {
                  const enriched = child.enrichmentStatuses != null &&
                    Object.values(child.enrichmentStatuses).some(s => s === 'Completed')
                  return (
                    <PosterImage
                      posterUrl={child.posterUrl}
                      name={child.name}
                      imgClassName={styles.childPoster}
                      placeholderContent={enriched
                        ? <span className={styles.childNoArt}>No art</span>
                        : (child.number ?? child.name.charAt(0))}
                    />
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

      {/* ── Additional Images: type-scoped gallery/promote modal ──────────── */}
      {gallerySlot && (
        <SlotGalleryModal
          slotLabel={gallerySlot.slotLabel}
          images={gallerySlot.images}
          startIndex={gallerySlot.startIndex}
          onClose={() => setGallerySlot(null)}
          overrides={item.overrides}
          onSet={(slot, img) => overrideSetMut.mutate({
            slot, url: img.url, pluginId: img.pluginId, sourceType: img.slot,
          })}
          onClear={(slot) => overrideClearMut.mutate(slot)}
          pendingSlot={pendingSlot}
        />
      )}

      {/* ── Manual "add image from URL" modal ──────────────────────────────── */}
      {manualImageOpen && (
        <ManualImageUrlModal
          onClose={() => setManualImageOpen(false)}
          overrides={item.overrides}
          onSet={(slot, url) => overrideSetMut.mutate({ slot, url })}
          onClear={(slot) => overrideClearMut.mutate(slot)}
          pendingSlot={pendingSlot}
        />
      )}

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
          {allImages[lightboxIdx]?.url && (
            <ImageSlotControls
              imageUrl={allImages[lightboxIdx]!.url}
              overrides={item.overrides}
              onSet={slot => overrideSetMut.mutate({
                slot,
                url: allImages[lightboxIdx]!.url,
                pluginId: slotLookup.get(allImages[lightboxIdx]!.url)?.pluginId,
                sourceType: slotLookup.get(allImages[lightboxIdx]!.url)?.slot,
              })}
              onClear={slot => overrideClearMut.mutate(slot)}
              pendingSlot={pendingSlot}
            />
          )}
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

      {mergeTarget && item && (
        <MergeModal
          itemA={{
            id: item.id,
            name: item.name,
            posterUrl: item.posterUrl,
            mediaTypeName: item.mediaTypeName,
            year: item.year,
            runtimeMinutes: item.runtimeMinutes,
            overview: item.overview,
            filePath: item.fileScannerMeta?.filePath ?? null,
          }}
          itemB={mergeTarget}
          onClose={() => setMergeTarget(null)}
          onMerged={(winnerId) => {
            setMergeTarget(null)
            qc.invalidateQueries({ queryKey: ['media', String(winnerId)] })
            navigate(`/media/${winnerId}`, { replace: true })
          }}
        />
      )}
    </div>
  )
}
