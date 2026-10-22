import { neutralStatusLabel } from '@/utils/typeWording'
import { useState, useMemo, useEffect, useRef, useCallback, memo } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { useQuery, useQueries, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  getLibrarySections, getLibrarySectionPage, getLibraryEntryForMedia,
  updateLibraryEntry, removeFromLibrary,
} from '@/api/library'
import { deleteMedia } from '@/api/media'
import { useCanDeleteMedia } from '@/hooks/useCanDeleteMedia'
import type { LibraryEntry, LibraryStatus } from '@/types'
import { loadSortSettings, getIndexLetter } from '@/utils/sortSettings'
import { loadPrefs, savePrefs, DEFAULT_PREFS, type LibraryPrefs } from '@/utils/libraryPrefs'
import { posterProgressPercent } from '@/utils/posterProgress'
import styles from './LibraryPage.module.css'
import { IconHdd } from '@/components/FileStatusIcons'
import { PosterImage } from '@/components/PosterImage'
import { AlphabetScrollIndicator } from '@/components/AlphabetScrollIndicator'

// ── Constants ────────────────────────────────────────────────────────────────

const STATUS_OPTIONS: LibraryStatus[] = [
  'Unwatched', 'Watching', 'PlanToWatch', 'Completed', 'Dropped', 'OnHold', 'Rewatching',
]

// The library mixes every media type, so its statuses carry no action word ("Watching" would be wrong for music).
const STATUS_LABELS: Record<LibraryStatus, string> = {
  Unwatched: neutralStatusLabel('Unwatched'),
  Watching: neutralStatusLabel('Watching'),
  PlanToWatch: neutralStatusLabel('PlanToWatch'),
  Completed: neutralStatusLabel('Completed'),
  Dropped: neutralStatusLabel('Dropped'),
  OnHold: neutralStatusLabel('OnHold'),
  Rewatching: neutralStatusLabel('Rewatching'),
}

const PAGE_SIZES = { minimal: 6, medium: 24, maximal: 100, all: Infinity } as const
type PageSizePreset = keyof typeof PAGE_SIZES

type SortField = 'name' | 'year' | 'dateAdded' | 'rating' | 'status'
type SortDir = 'asc' | 'desc'

const SORT_OPTIONS: { value: string; label: string }[] = [
  { value: 'name-asc',       label: 'Name A–Z' },
  { value: 'name-desc',      label: 'Name Z–A' },
  { value: 'year-desc',      label: 'Year (newest first)' },
  { value: 'year-asc',       label: 'Year (oldest first)' },
  { value: 'dateAdded-desc', label: 'Date Added (newest)' },
  { value: 'dateAdded-asc',  label: 'Date Added (oldest)' },
  { value: 'rating-desc',    label: 'My Rating (highest)' },
  { value: 'rating-asc',     label: 'My Rating (lowest)' },
  { value: 'status-asc',     label: 'Status A–Z' },
]

// ── Preferences (localStorage) ───────────────────────────────────────────────
// LibraryPrefs, DEFAULT_PREFS, loadPrefs, savePrefs live in @/utils/libraryPrefs
// so LibrarySettingsPage can import them without pulling in the full LibraryPage bundle.
export type { LibraryPrefs } from '@/utils/libraryPrefs'
export { DEFAULT_PREFS, loadPrefs, savePrefs } from '@/utils/libraryPrefs'

const PRESETS_KEY = 'chronicle_library_presets'

export interface LibraryPreset {
  id: string
  name: string
  prefs: LibraryPrefs
}

export function loadPresets(): LibraryPreset[] {
  try {
    const raw = localStorage.getItem(PRESETS_KEY)
    if (raw) return JSON.parse(raw)
  } catch { /* ignore */ }
  return []
}

export function savePresets(presets: LibraryPreset[]) {
  localStorage.setItem(PRESETS_KEY, JSON.stringify(presets))
}

// ── Helpers ──────────────────────────────────────────────────────────────────

function prefsAreDefault(p: LibraryPrefs): boolean {
  return (
    p.sortBy === DEFAULT_PREFS.sortBy &&
    p.sortDir === DEFAULT_PREFS.sortDir &&
    p.statusFilter === DEFAULT_PREFS.statusFilter &&
    p.pageSizePreset === DEFAULT_PREFS.pageSizePreset
  )
}

function describePrefs(p: LibraryPrefs): string {
  const parts: string[] = []
  if (p.statusFilter) parts.push(STATUS_LABELS[p.statusFilter])
  else parts.push('All statuses')
  const sortLabel = SORT_OPTIONS.find(o => o.value === `${p.sortBy}-${p.sortDir}`)?.label ?? p.sortBy
  parts.push(sortLabel)
  parts.push(p.pageSizePreset === 'all' ? 'Show all' : `${PAGE_SIZES[p.pageSizePreset]} per section`)
  return parts.join(' · ')
}

// ── Component ────────────────────────────────────────────────────────────────

/** One library card. Memoised: a card re-renders only when its own entry or selection state changes, so opening or closing
 *  a section, or a data refresh in another one, no longer re-renders every card on the page (seconds, on a phone). */
const LibraryCard = memo(function LibraryCard({ entry, navState, selectMode, selected, letterSort, onToggleSelected, onStatusChange, onRemove }: {
  entry: LibraryEntry
  navState: { listIds: number[]; listLabel: string }
  selectMode: boolean
  selected: boolean
  letterSort: ReturnType<typeof loadSortSettings> | null
  onToggleSelected: (mediaId: number) => void
  onStatusChange: (entryId: number, status: LibraryStatus) => void
  onRemove: (entryId: number) => void
}) {
  return (
        <div
          key={entry.id}
          id={`media-${entry.mediaItem.id}`}
          data-letter={letterSort ? getIndexLetter(entry.mediaItem.name, letterSort) : undefined}
          className={[
            styles.card,
            entry.mediaItem.isCollectionContainer ? styles.cardCollection : '',
            // hasMetadataOnly, not isStub -- isStub is only true for a collection's
            // own auto-created placeholder rows. A SIMKL/Trakt watch-history import is
            // a real (non-stub) item with a genuine library entry and still has no
            // physical file at all; hasMetadataOnly is the field that actually answers
            // "is this missing a file" for every case, stub or not. Mirrors the same
            // fix already made to the collection detail page's own "Not in Library"
            // badge (CollectionMetadataBox.tsx).
            entry.mediaItem.hasMetadataOnly ? styles.cardStub : '',
            selectMode && selected ? styles.cardSelected : '',
          ].filter(Boolean).join(' ')}
          onClick={selectMode ? () => onToggleSelected(entry.mediaItem.id) : undefined}
          style={selectMode ? { cursor: 'pointer' } : undefined}
        >
          {selectMode ? (
            <div className={styles.posterLink} style={{ position: 'relative' }}>
              <div className={styles.poster}>
                <PosterImage posterUrl={entry.mediaItem.posterUrl} name={entry.mediaItem.name} lazy progressPercent={posterProgressPercent(entry.status, entry.resumePositionPercent, entry.lastKnownProgressPercent)} />
                {entry.mediaItem.hasPhysicalFile && (
                  <div className={styles.fileIndicator}>
                    <span className={styles.fileIcon} title="Has physical file on disk"><IconHdd /></span>
                  </div>
                )}
                {entry.mediaItem.isCollectionContainer && (
                  <div className={styles.collectionBadge}>Collection</div>
                )}
                {entry.mediaItem.hasMetadataOnly && (
                  <div className={styles.stubBadge}>Missing</div>
                )}
                {entry.mediaItem.deathDate && (
                  <div className={styles.deceasedBadge} title={`Deceased ${new Date(entry.mediaItem.deathDate).getUTCFullYear()}`}>
                    Deceased
                  </div>
                )}
              </div>
              {selected && (
                <div className={styles.selectOverlay}>✓</div>
              )}
            </div>
          ) : (
            <Link to={`/media/${entry.mediaItem.id}`} state={navState} className={styles.posterLink}>
              <div className={styles.poster}>
                <PosterImage posterUrl={entry.mediaItem.posterUrl} name={entry.mediaItem.name} lazy progressPercent={posterProgressPercent(entry.status, entry.resumePositionPercent, entry.lastKnownProgressPercent)} />
                {entry.mediaItem.hasPhysicalFile && (
                  <div className={styles.fileIndicator}>
                    <span className={styles.fileIcon} title="Has physical file on disk"><IconHdd /></span>
                  </div>
                )}
                {entry.mediaItem.isCollectionContainer && (
                  <div className={styles.collectionBadge}>Collection</div>
                )}
                {entry.mediaItem.hasMetadataOnly && (
                  <div className={styles.stubBadge}>Missing</div>
                )}
                {entry.mediaItem.deathDate && (
                  <div className={styles.deceasedBadge} title={`Deceased ${new Date(entry.mediaItem.deathDate).getUTCFullYear()}`}>
                    Deceased
                  </div>
                )}
              </div>
            </Link>
          )}
          <div className={styles.info}>
            {/* A tracked episode/season's own name is often a generic code (e.g.
                "S01E01") that's meaningless without knowing which show it's from --
                ancestors[0] is the root show, so show it as a prefix line whenever
                this entry is anything other than a standalone root-level item. */}
            {entry.mediaItem.ancestors && entry.mediaItem.ancestors.length > 0 && (
              <div className={styles.showName}>{entry.mediaItem.ancestors[0].name}</div>
            )}
            {selectMode ? (
              <div className={styles.name}>{entry.mediaItem.name}</div>
            ) : (
              <Link to={`/media/${entry.mediaItem.id}`} state={navState} className={styles.nameLink}>
                <div className={styles.name}>{entry.mediaItem.name}</div>
              </Link>
            )}
            <div className={styles.metaRow}>
              {entry.mediaItem.year && <span className={styles.year}>{entry.mediaItem.year}</span>}
              {entry.mediaItem.resolvedMetadata?.rating != null && (
                <span className={styles.rating} title="Public rating">★ {entry.mediaItem.resolvedMetadata.rating.toFixed(1)}</span>
              )}
              {entry.userRating != null && (
                <span className={styles.userRating} title={`My Rating${entry.userRatingSource ? ` (via ${entry.userRatingSource})` : ''}`}>♥ {entry.userRating}</span>
              )}
            </div>
            {!selectMode && (
              <>
                {!entry.mediaItem.isCollectionContainer && (
                  <select
                    className={styles.statusSelect}
                    value={entry.status}
                    onChange={e =>
                      onStatusChange(entry.id, e.target.value as LibraryStatus)
                    }
                  >
                    {STATUS_OPTIONS.map(s => (
                      <option key={s} value={s}>{STATUS_LABELS[s]}</option>
                    ))}
                  </select>
                )}
                <button
                  className={styles.removeBtn}
                  onClick={() => onRemove(entry.id)}
                >
                  Remove
                </button>
              </>
            )}
          </div>
        </div>

  )
})

/** The grid of one section. Memoised on its entries, so the grid of a section whose data did not change is skipped entirely. */
const SectionCards = memo(function SectionCards({ entries, typeName, selectMode, selectedIds, letterSort, onToggleSelected, onStatusChange, onRemove }: {
  entries: LibraryEntry[]
  typeName: string
  selectMode: boolean
  selectedIds: Set<number>
  letterSort: ReturnType<typeof loadSortSettings> | null
  onToggleSelected: (mediaId: number) => void
  onStatusChange: (entryId: number, status: LibraryStatus) => void
  onRemove: (entryId: number) => void
}) {
  const navState = useMemo(() => ({ listIds: entries.map(e => e.mediaItem.id), listLabel: typeName }), [entries, typeName])
  return (
    <>
      {entries.map(entry => (
        <LibraryCard
          key={entry.id}
          entry={entry}
          navState={navState}
          selectMode={selectMode}
          selected={selectMode && selectedIds.has(entry.mediaItem.id)}
          letterSort={letterSort}
          onToggleSelected={onToggleSelected}
          onStatusChange={onStatusChange}
          onRemove={onRemove}
        />
      ))}
    </>
  )
})

export default function LibraryPage() {
  const [prefs, setPrefsState] = useState<LibraryPrefs>(loadPrefs)
  const [presets, setPresets] = useState<LibraryPreset[]>(loadPresets)
  const [expanded, setExpanded] = useState<Record<string, boolean>>({})
  const [sectionPage, setSectionPage] = useState<Record<string, number>>({})
  const [showSavePreset, setShowSavePreset] = useState(false)
  const [presetName, setPresetName] = useState('')
  const [collapsedSections, setCollapsedSections] = useState<Record<string, boolean>>(() => {
    try {
      const stored = localStorage.getItem('chronicle.library.collapsed')
      return stored ? JSON.parse(stored) : {}
    } catch {
      return {}
    }
  })
  const [selectMode, setSelectMode] = useState(false)
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set())
  const [deleteConfirm, setDeleteConfirm] = useState(false)
  const canDelete = useCanDeleteMedia()
  const [isDeleting, setIsDeleting] = useState(false)

  const qc = useQueryClient()

  function setPrefs(updates: Partial<LibraryPrefs>) {
    const next = { ...prefs, ...updates }
    setPrefsState(next)
    savePrefs(next)
  }

  function resetPrefs() {
    setPrefsState({ ...DEFAULT_PREFS })
    savePrefs({ ...DEFAULT_PREFS })
    setExpanded({})
    setSectionPage({})
  }

  function applyPreset(preset: LibraryPreset) {
    setPrefsState({ ...preset.prefs })
    savePrefs({ ...preset.prefs })
    setExpanded({})
    setSectionPage({})
  }

  function toggleSection(mediaTypeName: string) {
    setCollapsedSections(prev => {
      const next = { ...prev, [mediaTypeName]: !prev[mediaTypeName] }
      localStorage.setItem('chronicle.library.collapsed', JSON.stringify(next))
      return next
    })
  }

  function enterSelectMode() {
    setSelectMode(true)
    setSelectedIds(new Set())
    setDeleteConfirm(false)
  }

  function exitSelectMode() {
    setSelectMode(false)
    setSelectedIds(new Set())
    setDeleteConfirm(false)
  }

  const toggleSelected = useCallback((mediaId: number) => {
    setSelectedIds(prev => {
      const next = new Set(prev)
      if (next.has(mediaId)) next.delete(mediaId)
      else next.add(mediaId)
      return next
    })
  }, [])

  function selectAll() {
    // Only what has been loaded: sections fetch just their visible page, so "all" means all on screen.
    setSelectedIds(new Set(Array.from(grouped.values()).flat().map(e => e.mediaItem.id)))
  }

  async function confirmDelete() {
    setIsDeleting(true)
    try {
      for (const id of selectedIds) {
        await deleteMedia(id)
      }
      qc.invalidateQueries({ queryKey: ['library'] })
      qc.invalidateQueries({ queryKey: ['media'] })
    } finally {
      setIsDeleting(false)
      exitSelectMode()
    }
  }

  function handleSavePreset() {
    if (!presetName.trim()) return
    const preset: LibraryPreset = {
      id: crypto.randomUUID(),
      name: presetName.trim(),
      prefs: { ...prefs },
    }
    const next = [...presets, preset]
    setPresets(next)
    savePresets(next)
    setShowSavePreset(false)
    setPresetName('')
  }

  // The page asks the server for the section headers (media type + count) first, then for one
  // page of each open section -- never the whole catalog. Sorting and the status filter are applied
  // by the server so a page is a true slice of the sorted list.
  const includeMoviesInCollections = !prefs.groupMoviesIntoCollections
  const { data: sections = [], isLoading, isFetching } = useQuery({
    queryKey: ['library', 'sections', { status: prefs.statusFilter ?? null, includeMoviesInCollections }],
    queryFn: () => getLibrarySections(prefs.statusFilter, includeMoviesInCollections),
    staleTime: 5 * 60 * 1000,          // cached data considered fresh for 5 min
    placeholderData: (prev) => prev,    // keep showing previous data while revalidating
  })

  const updateMut = useMutation({
    mutationFn: ({ id, status }: { id: number; status: LibraryStatus }) =>
      updateLibraryEntry(id, { status }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['library'] }),
  })

  const removeMut = useMutation({
    mutationFn: (id: number) => removeFromLibrary(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['library'] }),
  })

  // Stable identities (mutate never changes), so a section's cards are not re-rendered by an unrelated state change.
  const { mutate: mutateStatus } = updateMut
  const { mutate: mutateRemove } = removeMut
  const changeStatus = useCallback((entryId: number, status: LibraryStatus) => mutateStatus({ id: entryId, status }), [mutateStatus])
  const removeEntry = useCallback((entryId: number) => {
    if (confirm('Remove from library?')) mutateRemove(entryId)
  }, [mutateRemove])

  const sortSettings = useMemo(() => loadSortSettings(), [])
  const pageSize = PAGE_SIZES[prefs.pageSizePreset]
  const isDefault = prefsAreDefault(prefs)

  const orderedSections = useMemo(
    () => [...sections].sort((a, b) => a.name.localeCompare(b.name)),
    [sections],
  )
  const mediaTypeNames = useMemo(() => orderedSections.map(sec => sec.name), [orderedSections])
  const sectionByName = useMemo(() => new Map(orderedSections.map(sec => [sec.name, sec])), [orderedSections])

  // One query per open section. A collapsed section loads nothing until it is opened.
  const sectionResults = useQueries({
    queries: orderedSections.map(sec => {
      const wholeSection = pageSize === Infinity || (expanded[sec.name] ?? false)
      const page = wholeSection ? 1 : (sectionPage[sec.name] ?? 0) + 1
      const perPage = wholeSection ? 0 : pageSize
      return {
        queryKey: ['library', 'section', {
          mediaTypeId: sec.mediaTypeId, status: prefs.statusFilter ?? null, includeMoviesInCollections,
          page, perPage, sortBy: prefs.sortBy, sortDir: prefs.sortDir,
          articles: sortSettings.ignoreArticles ? sortSettings.ignoredArticles : null,
        }],
        queryFn: () => getLibrarySectionPage({
          mediaTypeId: sec.mediaTypeId,
          status: prefs.statusFilter,
          includeMoviesInCollections,
          page,
          perPage,
          sortBy: prefs.sortBy,
          sortDir: prefs.sortDir,
          ignoreArticles: sortSettings.ignoreArticles ? sortSettings.ignoredArticles : undefined,
        }),
        enabled: !(collapsedSections[sec.name] ?? false),
        staleTime: 5 * 60 * 1000,
        placeholderData: (prev: LibraryEntry[] | undefined) => prev,
      }
    }),
  })

  // Entries currently loaded for each section (its visible page, or the whole section when expanded).
  const grouped = useMemo(() => {
    const map = new Map<string, LibraryEntry[]>()
    orderedSections.forEach((sec, i) => map.set(sec.name, sectionResults[i]?.data ?? []))
    return map
  }, [orderedSections, sectionResults.map(r => r.dataUpdatedAt).join(',')])
  const sectionsLoading = sectionResults.some(r => r.isLoading)

  const location = useLocation()

  // Coming back from a detail page ("#media-42"): find the item's card and scroll to it. If its
  // section hasn't loaded that card (collapsed, or on another page), look up which section it
  // belongs to and open that section whole so the card exists to scroll to.
  const locatedFor = useRef<number | null>(null)
  // The visit (navigation) that has already been scrolled to its #media-<id> target. The hash stays in the address while the
  // person browses on, and this effect re-runs on every data refresh (a watch in progress refreshes the library every few
  // seconds): without this it pulled the page back to that item each time, mid-scroll.
  const scrolledFor = useRef<string | null>(null)
  useEffect(() => {
    if (isLoading || sectionsLoading) return
    const hash = location.hash  // e.g. "#media-42"
    if (!hash.startsWith('#media-')) return
    const targetId = parseInt(hash.slice('#media-'.length), 10)
    if (isNaN(targetId)) return

    let targetTypeName: string | undefined
    for (const [typeName, entries] of grouped) {
      if (entries.some(e => e.mediaItem.id === targetId)) {
        targetTypeName = typeName
        break
      }
    }

    if (!targetTypeName) {
      if (locatedFor.current === targetId) return
      locatedFor.current = targetId
      let cancelled = false
      void getLibraryEntryForMedia(targetId).then(entry => {
        const name = entry?.mediaItem.mediaTypeName
        if (cancelled || !name) return
        setCollapsedSections(prev => {
          if (!prev[name]) return prev
          const next = { ...prev, [name]: false }
          localStorage.setItem('chronicle.library.collapsed', JSON.stringify(next))
          return next
        })
        setExpanded(prev => ({ ...prev, [name]: true }))
      }).catch(() => { /* not in this library view; nothing to scroll to */ })
      return () => { cancelled = true }
    }
    const resolvedTypeName = targetTypeName

    setCollapsedSections(prev => {
      if (!prev[resolvedTypeName]) return prev
      const next = { ...prev, [resolvedTypeName]: false }
      localStorage.setItem('chronicle.library.collapsed', JSON.stringify(next))
      return next
    })

    // Scroll after images have had time to load -- instant behavior avoids
    // mid-animation layout shifts from lazy-loaded posters above the target. Once per visit, never again on later refreshes.
    const visit = `${location.key}|${targetId}`
    if (scrolledFor.current === visit) return
    const timer = setTimeout(() => {
      const card = document.getElementById(`media-${targetId}`)
      if (!card) return
      card.scrollIntoView({ behavior: 'instant', block: 'center' })
      scrolledFor.current = visit
    }, 500)
    return () => clearTimeout(timer)
  }, [isLoading, sectionsLoading, location.hash, grouped])

  return (
    <div className={styles.page}>
      <AlphabetScrollIndicator selector="[data-letter]" enabled={prefs.sortBy === 'name'} />

      {/* ── Controls ── */}
      <div className={styles.controls}>

        {/* Heading row */}
        <div className={styles.controlsTop}>
          <h2 className={styles.heading}>Library</h2>
          <div className={styles.controlsActions}>
            {/* Select mode toggle */}
            {!selectMode ? (
              <button className={styles.actionBtn} onClick={enterSelectMode}>Select</button>
            ) : (
              <>
                <button className={styles.actionBtn} onClick={selectAll}>Select All</button>
                {canDelete && (
                  <button
                    className={styles.deleteModeBtn}
                    disabled={selectedIds.size === 0 || isDeleting}
                    onClick={() => setDeleteConfirm(true)}
                  >
                    Delete ({selectedIds.size})
                  </button>
                )}
                <button className={styles.cancelBtn} onClick={exitSelectMode}>✕ Cancel</button>
              </>
            )}
            {presets.length > 0 && (
              <select
                className={styles.presetSelect}
                value=""
                onChange={e => {
                  const p = presets.find(x => x.id === e.target.value)
                  if (p) applyPreset(p)
                }}
              >
                <option value="" disabled>Apply preset…</option>
                {presets.map(p => (
                  <option key={p.id} value={p.id}>{p.name}</option>
                ))}
              </select>
            )}
            {!showSavePreset && (
              <button
                className={styles.actionBtn}
                onClick={() => { setShowSavePreset(true); setPresetName('') }}
              >
                Save as Preset
              </button>
            )}
            {!isDefault && (
              <button className={styles.resetBtn} onClick={resetPrefs}>Reset</button>
            )}
            <Link to="/settings/library" className={styles.settingsLink}>Manage Presets</Link>
          </div>
        </div>

        {/* Save preset form */}
        {showSavePreset && (
          <div className={styles.savePresetRow}>
            <span className={styles.savePresetDesc}>{describePrefs(prefs)}</span>
            <input
              className={styles.presetNameInput}
              value={presetName}
              onChange={e => setPresetName(e.target.value)}
              placeholder="Preset name…"
              autoFocus
              onKeyDown={e => {
                if (e.key === 'Enter') handleSavePreset()
                if (e.key === 'Escape') setShowSavePreset(false)
              }}
            />
            <button
              className={styles.actionBtn}
              onClick={handleSavePreset}
              disabled={!presetName.trim()}
            >Save</button>
            <button className={styles.cancelBtn} onClick={() => setShowSavePreset(false)}>Cancel</button>
          </div>
        )}

        {/* Status filter row */}
        <div className={styles.filterRow}>
          <span className={styles.rowLabel}>Status</span>
          <div className={styles.filterBtns}>
            <button
              className={prefs.statusFilter === undefined ? styles.filterActive : styles.filter}
              onClick={() => setPrefs({ statusFilter: undefined })}
            >All</button>
            {STATUS_OPTIONS.map(s => (
              <button
                key={s}
                className={prefs.statusFilter === s ? styles.filterActive : styles.filter}
                onClick={() => setPrefs({ statusFilter: s })}
              >{STATUS_LABELS[s]}</button>
            ))}
          </div>
        </div>


        {/* Sort + page size row */}
        <div className={styles.sortRow}>
          <div className={styles.sortGroup}>
            <span className={styles.rowLabel}>Sort</span>
            <select
              className={styles.sortSelect}
              value={`${prefs.sortBy}-${prefs.sortDir}`}
              onChange={e => {
                const [sortBy, sortDir] = e.target.value.split('-') as [SortField, SortDir]
                setPrefs({ sortBy, sortDir })
              }}
            >
              {SORT_OPTIONS.map(o => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </select>
          </div>
          <div className={styles.pageSizeGroup}>
            <span className={styles.rowLabel}>Per section</span>
            {(Object.keys(PAGE_SIZES) as PageSizePreset[]).map(preset => (
              <button
                key={preset}
                className={prefs.pageSizePreset === preset ? styles.filterActive : styles.filter}
                onClick={() => { setPrefs({ pageSizePreset: preset }); setExpanded({}); setSectionPage({}) }}
              >
                {preset === 'minimal' ? 'Few (6)' : preset === 'medium' ? 'Medium (24)' : preset === 'maximal' ? 'Many (100)' : 'All'}
              </button>
            ))}
          </div>
        </div>
      </div>

      {/* ── Delete confirmation modal ── */}
      {deleteConfirm && (
        <div className={styles.deleteModal}>
          <div className={styles.deleteModalBox}>
            <p className={styles.deleteModalText}>
              Delete <strong>{selectedIds.size}</strong> item{selectedIds.size !== 1 ? 's' : ''}?
              This cannot be undone.
            </p>
            <div className={styles.deleteModalActions}>
              <button
                className={styles.cancelBtn}
                onClick={() => setDeleteConfirm(false)}
                disabled={isDeleting}
              >
                Cancel
              </button>
              <button
                className={styles.deleteConfirmOk}
                onClick={confirmDelete}
                disabled={isDeleting}
              >
                {isDeleting ? 'Deleting…' : 'Delete'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ── Content ── */}
      {isLoading && <p className={styles.empty}>Loading…</p>}
      {isFetching && !isLoading && (
        <div className={styles.refreshBar} aria-label="Refreshing library…" />
      )}

      {!isLoading && sections.length === 0 && !prefs.statusFilter && (
        <p className={styles.empty}>No items in your library yet.</p>
      )}

      {!isLoading && sections.length === 0 && prefs.statusFilter && (
        <p className={styles.empty}>No items match the current filter.</p>
      )}

      {mediaTypeNames.map(typeName => {
        const typeCount = sectionByName.get(typeName)!.count
        const sectionLoading = sectionResults[mediaTypeNames.indexOf(typeName)]?.isLoading ?? false
        const isExpanded = expanded[typeName] ?? false
        const isCollapsed = collapsedSections[typeName] ?? false
        const currentPage = sectionPage[typeName] ?? 0
        const totalPages = pageSize === Infinity ? 1 : Math.ceil(typeCount / pageSize)
        const visible = grouped.get(typeName) ?? []
        const hasMore = pageSize !== Infinity && typeCount > pageSize

        return (
          <section key={typeName} className={styles.section}>
            <div
              className={styles.sectionHeader}
              onClick={() => toggleSection(typeName)}
              role="button"
              tabIndex={0}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault()
                  toggleSection(typeName)
                }
              }}
            >
              <h3 className={styles.sectionTitle}>{typeName}</h3>
              <span className={styles.sectionCount}>{typeCount}</span>
              <span className={`${styles.chevron} ${isCollapsed ? styles.chevronCollapsed : ''}`}>
                ▾
              </span>
            </div>

            {!isCollapsed && sectionLoading && visible.length === 0 && (
              <p className={styles.empty}>Loading…</p>
            )}

            {!isCollapsed && <div className={styles.grid}>
              <SectionCards
                entries={visible}
                typeName={typeName}
                selectMode={selectMode}
                selectedIds={selectedIds}
                letterSort={prefs.sortBy === 'name' ? sortSettings : null}
                onToggleSelected={toggleSelected}
                onStatusChange={changeStatus}
                onRemove={removeEntry}
              />
            </div>}

            {!isCollapsed && hasMore && (
              <div className={styles.pageRow}>
                {!isExpanded && (
                  <>
                    <button
                      className={styles.pageBtn}
                      disabled={currentPage === 0}
                      onClick={() => setSectionPage(prev => ({ ...prev, [typeName]: currentPage - 1 }))}
                    >‹ Prev</button>
                    <span className={styles.pageInfo}>
                      {currentPage + 1} / {totalPages}
                    </span>
                    <button
                      className={styles.pageBtn}
                      disabled={currentPage >= totalPages - 1}
                      onClick={() => setSectionPage(prev => ({ ...prev, [typeName]: currentPage + 1 }))}
                    >Next ›</button>
                  </>
                )}
                <button
                  className={styles.showMoreBtn}
                  onClick={() => {
                    setExpanded(prev => ({ ...prev, [typeName]: !prev[typeName] }))
                    if (!isExpanded) setSectionPage(prev => ({ ...prev, [typeName]: 0 }))
                  }}
                >
                  {isExpanded ? `Show fewer` : `Show all ${typeCount} items`}
                </button>
              </div>
            )}
          </section>
        )
      })}
    </div>
  )
}
