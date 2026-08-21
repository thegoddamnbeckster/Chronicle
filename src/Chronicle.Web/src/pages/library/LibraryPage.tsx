import { useState, useMemo, useEffect } from 'react'
import { Link, useLocation } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getLibrary, updateLibraryEntry, removeFromLibrary } from '@/api/library'
import { deleteMedia } from '@/api/media'
import type { LibraryEntry, LibraryStatus } from '@/types'
import { loadSortSettings, stripLeadingArticle, getIndexLetter } from '@/utils/sortSettings'
import { loadPrefs, savePrefs, DEFAULT_PREFS, type LibraryPrefs } from '@/utils/libraryPrefs'
import styles from './LibraryPage.module.css'
import { IconHdd } from '@/components/FileStatusIcons'
import { PosterImage } from '@/components/PosterImage'
import { AlphabetScrollIndicator } from '@/components/AlphabetScrollIndicator'

// ── Constants ────────────────────────────────────────────────────────────────

const STATUS_OPTIONS: LibraryStatus[] = [
  'Unwatched', 'Watching', 'PlanToWatch', 'Completed', 'Dropped', 'OnHold', 'Rewatching',
]

const STATUS_LABELS: Record<LibraryStatus, string> = {
  Unwatched: 'Unwatched',
  Watching: 'Watching',
  PlanToWatch: 'Plan to Watch',
  Completed: 'Completed',
  Dropped: 'Dropped',
  OnHold: 'On Hold',
  Rewatching: 'Rewatching',
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

function sortEntries(entries: LibraryEntry[], sortBy: SortField, sortDir: SortDir): LibraryEntry[] {
  const sortSettings = loadSortSettings()
  return [...entries].sort((a, b) => {
    let cmp = 0
    switch (sortBy) {
      case 'name': {
        const nameA = sortSettings.ignoreArticles
          ? stripLeadingArticle(a.mediaItem.name, sortSettings.ignoredArticles)
          : a.mediaItem.name
        const nameB = sortSettings.ignoreArticles
          ? stripLeadingArticle(b.mediaItem.name, sortSettings.ignoredArticles)
          : b.mediaItem.name
        cmp = nameA.localeCompare(nameB)
        break
      }
      case 'year':      cmp = (a.mediaItem.year ?? 0) - (b.mediaItem.year ?? 0); break
      case 'dateAdded': cmp = new Date(a.addedAt).getTime() - new Date(b.addedAt).getTime(); break
      case 'rating':    cmp = (a.userRating ?? 0) - (b.userRating ?? 0); break
      case 'status':    cmp = a.status.localeCompare(b.status); break
    }
    return sortDir === 'asc' ? cmp : -cmp
  })
}

function groupByType(entries: LibraryEntry[]): Map<string, LibraryEntry[]> {
  const map = new Map<string, LibraryEntry[]>()
  for (const e of entries) {
    const key = e.mediaItem.mediaTypeName
    if (!map.has(key)) map.set(key, [])
    map.get(key)!.push(e)
  }
  return map
}

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

  function toggleSelected(mediaId: number) {
    setSelectedIds(prev => {
      const next = new Set(prev)
      if (next.has(mediaId)) next.delete(mediaId)
      else next.add(mediaId)
      return next
    })
  }

  function selectAll() {
    setSelectedIds(new Set(sorted.map(e => e.mediaItem.id)))
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

  const { data: allEntries = [], isLoading, isFetching } = useQuery({
    queryKey: ['library', 'all', {
      rootOnly: true,
      includeMoviesInCollections: !prefs.groupMoviesIntoCollections,
    }],
    queryFn: () => getLibrary(undefined, 1, 0, true, !prefs.groupMoviesIntoCollections),
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

  const filtered = useMemo(() => {
    if (!prefs.statusFilter) return allEntries
    return allEntries.filter(e => e.status === prefs.statusFilter)
  }, [allEntries, prefs.statusFilter])

  const sorted = useMemo(
    () => sortEntries(filtered, prefs.sortBy, prefs.sortDir),
    [filtered, prefs.sortBy, prefs.sortDir],
  )

  const sortSettings = useMemo(() => loadSortSettings(), [])
  const grouped = useMemo(() => groupByType(sorted), [sorted])
  const mediaTypeNames = useMemo(() => Array.from(grouped.keys()).sort(), [grouped])
  const pageSize = PAGE_SIZES[prefs.pageSizePreset]
  const isDefault = prefsAreDefault(prefs)

  const location = useLocation()

  useEffect(() => {
    if (isLoading) return
    const hash = location.hash  // e.g. "#media-42"
    if (!hash.startsWith('#media-')) return
    const targetId = parseInt(hash.slice('#media-'.length), 10)
    if (isNaN(targetId)) return

    // Find which section this item belongs to
    let targetTypeName: string | undefined
    for (const [typeName, entries] of grouped) {
      if (entries.some(e => e.mediaItem.id === targetId)) {
        targetTypeName = typeName
        break
      }
    }
    if (!targetTypeName) return
    const resolvedTypeName = targetTypeName   // narrow from string | undefined to string

    // Un-collapse the section if it is collapsed
    setCollapsedSections(prev => {
      if (!prev[resolvedTypeName]) return prev
      const next = { ...prev, [resolvedTypeName]: false }
      localStorage.setItem('chronicle.library.collapsed', JSON.stringify(next))
      return next
    })

    // Expand the section if the item is beyond the current page size
    const typeEntries = grouped.get(resolvedTypeName)!
    const itemIndex = typeEntries.findIndex(e => e.mediaItem.id === targetId)
    if (pageSize !== Infinity && itemIndex >= pageSize) {
      setExpanded(prev => ({ ...prev, [resolvedTypeName]: true }))
    }

    // Scroll after images have had time to load — instant behavior avoids
    // mid-animation layout shifts from lazy-loaded posters above the target.
    const timer = setTimeout(() => {
      document.getElementById(`media-${targetId}`)?.scrollIntoView({
        behavior: 'instant',
        block: 'center',
      })
    }, 500)
    return () => clearTimeout(timer)
  }, [isLoading, location.hash, grouped, pageSize])

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
                <button
                  className={styles.deleteModeBtn}
                  disabled={selectedIds.size === 0 || isDeleting}
                  onClick={() => setDeleteConfirm(true)}
                >
                  Delete ({selectedIds.size})
                </button>
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

      {!isLoading && allEntries.length === 0 && (
        <p className={styles.empty}>No items in your library yet.</p>
      )}

      {!isLoading && allEntries.length > 0 && sorted.length === 0 && (
        <p className={styles.empty}>No items match the current filter.</p>
      )}

      {mediaTypeNames.map(typeName => {
        const typeEntries = grouped.get(typeName)!
        const isExpanded = expanded[typeName] ?? false
        const isCollapsed = collapsedSections[typeName] ?? false
        const currentPage = sectionPage[typeName] ?? 0
        const totalPages = pageSize === Infinity ? 1 : Math.ceil(typeEntries.length / pageSize)
        const visible = pageSize === Infinity
          ? typeEntries
          : isExpanded
            ? typeEntries
            : typeEntries.slice(currentPage * pageSize, (currentPage + 1) * pageSize)
        const hasMore = pageSize !== Infinity && typeEntries.length > pageSize
        const sectionNavState = {
          listIds: visible.map(e => e.mediaItem.id),
          listLabel: typeName,
        }

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
              <span className={styles.sectionCount}>{typeEntries.length}</span>
              <span className={`${styles.chevron} ${isCollapsed ? styles.chevronCollapsed : ''}`}>
                ▾
              </span>
            </div>

            {!isCollapsed && <div className={styles.grid}>
              {visible.map(entry => (
                <div
                  key={entry.id}
                  id={`media-${entry.mediaItem.id}`}
                  data-letter={prefs.sortBy === 'name' ? getIndexLetter(entry.mediaItem.name, sortSettings) : undefined}
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
                    selectMode && selectedIds.has(entry.mediaItem.id) ? styles.cardSelected : '',
                  ].filter(Boolean).join(' ')}
                  onClick={selectMode ? () => toggleSelected(entry.mediaItem.id) : undefined}
                  style={selectMode ? { cursor: 'pointer' } : undefined}
                >
                  {selectMode ? (
                    <div className={styles.posterLink} style={{ position: 'relative' }}>
                      <div className={styles.poster}>
                        <PosterImage posterUrl={entry.mediaItem.posterUrl} name={entry.mediaItem.name} lazy />
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
                      </div>
                      {selectedIds.has(entry.mediaItem.id) && (
                        <div className={styles.selectOverlay}>✓</div>
                      )}
                    </div>
                  ) : (
                    <Link to={`/media/${entry.mediaItem.id}`} state={sectionNavState} className={styles.posterLink}>
                      <div className={styles.poster}>
                        <PosterImage posterUrl={entry.mediaItem.posterUrl} name={entry.mediaItem.name} lazy />
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
                      <Link to={`/media/${entry.mediaItem.id}`} state={sectionNavState} className={styles.nameLink}>
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
                              updateMut.mutate({ id: entry.id, status: e.target.value as LibraryStatus })
                            }
                          >
                            {STATUS_OPTIONS.map(s => (
                              <option key={s} value={s}>{STATUS_LABELS[s]}</option>
                            ))}
                          </select>
                        )}
                        <button
                          className={styles.removeBtn}
                          onClick={() => {
                            if (confirm('Remove from library?')) removeMut.mutate(entry.id)
                          }}
                        >
                          Remove
                        </button>
                      </>
                    )}
                  </div>
                </div>
              ))}
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
                  {isExpanded ? `Show fewer` : `Show all ${typeEntries.length} items`}
                </button>
              </div>
            )}
          </section>
        )
      })}
    </div>
  )
}
