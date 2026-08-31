import { useCallback, useContext, useEffect, useRef, useState } from 'react'
import { useQuery, useInfiniteQuery } from '@tanstack/react-query'
import { useVirtualizer } from '@tanstack/react-virtual'
import { getPeople, getPersonRoles } from '@/api/people'
import { PersonCard } from '@/components/people/PersonCard'
import { AlphabetRail } from '@/components/people/AlphabetRail'
import { MainScrollContext } from '@/components/layout/Layout'
import {
  loadPeoplePrefs, savePeoplePrefs, type PeopleLibraryPrefs,
} from '@/utils/peopleLibraryPrefs'
import styles from './PeopleLibraryPage.module.css'

type SortOption = PeopleLibraryPrefs['sort']
type DeceasedFilter = PeopleLibraryPrefs['deceased']

// Fixed infinite-scroll page size -- per-user request (2026-08-31): "limiting the block
// to few, medium or many is not smart... remove those in favour of just using infinite
// scroll." A user-chosen preset added a filter/prefs dimension without adding any real
// control (infinite scroll keeps fetching regardless of the starting page size). One
// fixed size, not user-configurable.
const PEOPLE_PAGE_SIZE = 40

// Credit roles span every job title TMDB/Hardcover/etc. have ever supplied (hundreds --
// "'A' Camera Operator" through "Writers' Production") -- per-user request (2026-08-30):
// too many to show at once, so collapsed by default to this many chips.
const VISIBLE_ROLE_COUNT = 20

const SORT_OPTIONS: { value: SortOption; label: string }[] = [
  { value: 'name', label: 'Name A–Z' },
  { value: 'birthDate', label: 'Birth Date' },
  { value: 'createdAt', label: 'Recently Added' },
]

const DECEASED_OPTIONS: { value: DeceasedFilter; label: string }[] = [
  { value: 'either', label: 'All' },
  { value: 'living', label: 'Living' },
  { value: 'deceased', label: 'Deceased' },
]

// Virtualized grid geometry -- matches the previous plain-CSS-grid's own
// `minmax(170px, 1fr)` / 14px gap so the visual result is identical, just windowed.
const CARD_MIN_WIDTH = 170
const GRID_GAP = 14
// Per-user request (2026-08-30): a phone-width viewport was landing on a single, huge
// full-width card per row (CARD_MIN_WIDTH's own floor divides out to 1 column below ~330px
// usable width). MIN_COLUMNS overrides that floor -- cards shrink below CARD_MIN_WIDTH on a
// narrow screen instead, rather than ever going below 3 across.
const MIN_COLUMNS = 3
// Poster aspect-ratio 2:3 (see PersonCard.module.css) -> height = width * 1.5, plus the
// text info block below it and its own padding. Name now wraps up to 2 lines (per-user
// request 2026-08-30, PersonCard.module.css's -webkit-line-clamp: 2) instead of 1 -- roles
// and dates stay single-line-truncated -- so this is one line-height (~20px) taller than a
// strictly-single-line card would be. Keep in sync with PersonCard.module.css's own layout.
const CARD_INFO_HEIGHT = 84

/** Catalog-wide People grid -- every person credited on something in Chronicle, or added
 * directly, regardless of any single user's library (docs/plans/2026-08-28-people-section-
 * design.md Section 5). Not a fork of LibraryPage: no watch-status concept applies to a
 * person, so this mirrors that page's general shape only -- except for filters/sort/page-size
 * controls, which now deliberately DO mirror LibraryPage's own (utils/peopleLibraryPrefs.ts),
 * per-user request (2026-08-30): "I need the same kinds of controls that the library has.
 * similar filtering and the ability to save."
 *
 * Virtualized (only on-screen + a small overscan buffer ever exist as real DOM nodes) and
 * jump-to-letter (an A-Z rail plus a jump-search box, both backed by the server's own
 * jumpTo= parameter -- PeopleController.GetPeople) per a further request the same day:
 * "let whatever will let my phone scroll through the people as I want to scroll and will
 * let me jump into a mid point of the list of people as I need to without having to reload
 * the entire list." New pages auto-load as the virtualized window approaches the end of
 * what's already fetched -- no manual "Load More" click needed any more. */
export default function PeopleLibraryPage() {
  const [prefs, setPrefsState] = useState<PeopleLibraryPrefs>(loadPeoplePrefs)
  const [rolesExpanded, setRolesExpanded] = useState(false)
  // Ephemeral, not persisted -- a jump is a "go here now" action, not a sticky preference;
  // reloading the page should land wherever the persisted sort/filter/page-size normally
  // puts you, not stuck mid-jump from last time.
  const [jumpTarget, setJumpTarget] = useState<string | null>(null)
  const [jumpInput, setJumpInput] = useState('')

  function setPrefs(updates: Partial<PeopleLibraryPrefs>) {
    const next = { ...prefs, ...updates }
    setPrefsState(next)
    savePeoplePrefs(next) // synchronous, on every change -- same as LibraryPage's own setPrefs
  }

  function jumpToLetter(letter: string) {
    setJumpTarget(letter)
    setJumpInput('')
  }

  function submitJumpSearch(e: React.FormEvent) {
    e.preventDefault()
    if (jumpInput.trim()) setJumpTarget(jumpInput.trim())
  }

  function clearJump() {
    setJumpTarget(null)
    setJumpInput('')
  }

  const { data: roles = [] } = useQuery({ queryKey: ['person-roles'], queryFn: getPersonRoles })

  // Only a single role is sent server-side today (the API filters on one Role value).
  const primaryRole = prefs.role || undefined

  // Collapsed view always includes whichever role is currently active, even if it wouldn't
  // otherwise fall within the first VISIBLE_ROLE_COUNT -- the selected filter must never be
  // hidden behind the fold itself.
  const visibleRoles = rolesExpanded
    ? roles
    : (() => {
        const head = roles.slice(0, VISIBLE_ROLE_COUNT)
        if (primaryRole && !head.includes(primaryRole) && roles.includes(primaryRole)) {
          return [primaryRole, ...head.slice(0, VISIBLE_ROLE_COUNT - 1)]
        }
        return head
      })()

  // jumpTo is only meaningful for sort=name -- the server silently ignores it otherwise
  // (PeopleController.GetPeople), so there's no point sending it and no point keying the
  // query by it either in that case (avoids a pointless extra query-key identity).
  const effectiveJumpTo = prefs.sort === 'name' ? jumpTarget : null

  // Real server-side pagination with client-side accumulation across pages -- the queryKey
  // is filters + jump target, never the accumulated page count itself, so auto-loading the
  // next page appends onto data.pages without ever invalidating what's already rendered,
  // while an actual filter/sort/jump change correctly starts a fresh query from page 1 (at
  // the jump target, if any).
  const {
    data,
    isFetchingNextPage,
    hasNextPage,
    fetchNextPage,
  } = useInfiniteQuery({
    queryKey: ['people', prefs.sort, primaryRole, prefs.deceased, effectiveJumpTo],
    queryFn: ({ pageParam }) => getPeople({
      sort: prefs.sort,
      role: primaryRole,
      deceased: prefs.deceased === 'either' ? undefined : prefs.deceased === 'deceased',
      jumpTo: effectiveJumpTo ?? undefined,
      page: pageParam,
      perPage: PEOPLE_PAGE_SIZE,
    }),
    initialPageParam: 1,
    getNextPageParam: (lastPage, allPages) => {
      const fetchedSoFar = allPages.reduce((sum, page) => sum + page.items.length, 0)
      if (lastPage.total != null && fetchedSoFar >= lastPage.total) return undefined
      // total absent (shouldn't happen in practice, but stay safe): a short page means
      // there's nothing left to fetch.
      if (lastPage.items.length < PEOPLE_PAGE_SIZE) return undefined
      return allPages.length + 1
    },
  })

  const people = data?.pages.flatMap(page => page.items) ?? []
  const isInitialLoading = data === undefined
  // Carried as router state into PersonDetailPage so its "↑ People" / Prev / Next nav
  // (mirrors MediaDetailPage's own "↑ Library" + list nav) knows the full list currently
  // loaded here, same idea as LibraryPage's per-section listIds/listLabel.
  const peopleNavState = { listIds: people.map(p => p.id), listLabel: 'People' }

  function toggleRole(role: string) {
    setPrefs({ role: prefs.role === role ? '' : role })
  }

  // ── Virtualized grid ──────────────────────────────────────────────────────────
  // Scrolls/measures against the app shell's own <main> (Layout.tsx), not a nested
  // container of its own -- a nested scroll box would silently break useScrollRestoration's
  // existing scroll-position memory for this route.
  const mainScrollRef = useContext(MainScrollContext)
  const gridRef = useRef<HTMLDivElement>(null)
  const [columnsPerRow, setColumnsPerRow] = useState(MIN_COLUMNS)
  const [cardWidth, setCardWidth] = useState(CARD_MIN_WIDTH)
  // Mobile stretches MIN_COLUMNS cards to fill the row (cardWidth is the stretched size);
  // desktop keeps cardWidth fixed at CARD_MIN_WIDTH and just fits more columns -- the render
  // below needs to know which mode produced the current cardWidth to pick `1fr` vs a literal
  // px track size.
  const [stretchMode, setStretchMode] = useState(true)
  // gridRef's own height changes every time a new page of people loads (it's sized to
  // virtualizer.getTotalSize(), which grows with the list) -- ResizeObserver fires on ANY
  // size change, height included, so without this guard every page load during infinite
  // scroll was re-running the column/width logic and calling virtualizer.measure() even
  // though the width never moved. Confirmed root cause (2026-08-31) of a real stutter:
  // "it keeps repeating that block of 24" while scrolling -- each auto-loaded page forced
  // a virtualizer remeasure mid-scroll.
  const lastWidthRef = useRef<number | null>(null)

  useEffect(() => {
    const el = gridRef.current
    if (!el) return
    const observer = new ResizeObserver(entries => {
      const width = entries[0]?.contentRect.width
      if (!width) return
      if (width === lastWidthRef.current) return
      lastWidthRef.current = width
      // Same two-mode rule as MediaDetailPage's people section (per-user request,
      // 2026-08-30 -- "similar rules apply to the people list"): on mobile, exactly
      // MIN_COLUMNS cards stretched to fill the row; on desktop, a fixed card size with
      // as many columns as fit, not stretched into oversized cards. 768px matches this
      // app's other mobile breakpoint (MediaDetailPage.module.css, Layout's sidebar mode).
      if (width <= 768) {
        setColumnsPerRow(MIN_COLUMNS)
        setCardWidth((width - (MIN_COLUMNS - 1) * GRID_GAP) / MIN_COLUMNS)
        setStretchMode(true)
      } else {
        const cols = Math.max(MIN_COLUMNS, Math.floor((width + GRID_GAP) / (CARD_MIN_WIDTH + GRID_GAP)))
        setColumnsPerRow(cols)
        setCardWidth(CARD_MIN_WIDTH)
        setStretchMode(false)
      }
      // virtualizer is declared further below but is a stable object across renders
      // (@tanstack/react-virtual mutates it in place via setOptions rather than recreating
      // it) -- referencing it here, in a callback that only ever runs asynchronously on a
      // real resize event well after mount, is safe.
      virtualizer.measure()
    })
    observer.observe(el)
    return () => observer.disconnect()
    // people.length > 0: the grid <div ref={gridRef}> only exists once there's something to
    // show it (it's behind the isInitialLoading/empty conditional below) -- an effect with
    // [] deps would run once on mount, find gridRef.current still null at that point, and
    // silently never attach the observer at all once the div actually appears. Re-running
    // this effect whenever that flips true (first load, OR a filter change that goes from
    // zero results to some) is what lets it find the real element.
  }, [people.length > 0])

  const rowHeight = cardWidth * 1.5 + CARD_INFO_HEIGHT
  const rowCount = Math.ceil(people.length / columnsPerRow)

  // Confirmed root cause (2026-08-31) of "it keeps repeating that block of 24": this
  // component re-renders on every fetched page/loading-state change (routine for an
  // infinite-scroll page), and an inline `() => mainScrollRef.current` is a NEW function
  // identity every render. @tanstack/react-virtual tears down and re-attaches its scroll
  // listener whenever getScrollElement's identity changes -- so during active scrolling
  // (which itself triggers re-renders via isFetchingNextPage) it was resubscribing
  // constantly and never retaining a stable read on the real scroll offset, leaving the
  // virtualizer stuck rendering rows near the top regardless of actual scrollTop.
  // useCallback keeps the function itself stable across renders; it still always reads
  // the CURRENT mainScrollRef.current at call time, so this loses nothing.
  const getScrollElement = useCallback(() => mainScrollRef.current, [mainScrollRef])

  const virtualizer = useVirtualizer({
    count: rowCount,
    getScrollElement,
    estimateSize: () => rowHeight + GRID_GAP,
    overscan: 4,
  })

  const virtualRows = virtualizer.getVirtualItems()

  // Auto-load the next page as the virtualized window approaches the end of what's already
  // fetched -- per-user request (2026-08-30): "automatically loads new items as I need
  // them." No manual "Load More" click any more.
  useEffect(() => {
    const lastRow = virtualRows[virtualRows.length - 1]
    if (!lastRow) return
    if (lastRow.index >= rowCount - 3 && hasNextPage && !isFetchingNextPage) {
      fetchNextPage()
    }
  }, [virtualRows, rowCount, hasNextPage, isFetchingNextPage, fetchNextPage])

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1>People</h1>
      </div>

      {prefs.sort === 'name' && (
        <form className={styles.jumpRow} onSubmit={submitJumpSearch}>
          <input
            type="text"
            className={styles.jumpInput}
            placeholder="Jump to name…"
            value={jumpInput}
            onChange={e => setJumpInput(e.target.value)}
          />
          <button type="submit" className={styles.filter}>Jump</button>
          {jumpTarget && (
            <button type="button" className={styles.filter} onClick={clearJump}>
              Clear jump ({jumpTarget})
            </button>
          )}
        </form>
      )}

      {/* Deceased filter row -- same filterRow/filter/filterActive treatment as
          LibraryPage's own status filter. */}
      <div className={styles.filterRow}>
        <span className={styles.rowLabel}>Show</span>
        <div className={styles.filterBtns}>
          {DECEASED_OPTIONS.map(o => (
            <button
              key={o.value}
              type="button"
              className={prefs.deceased === o.value ? styles.filterActive : styles.filter}
              onClick={() => setPrefs({ deceased: o.value })}
            >
              {o.label}
            </button>
          ))}
        </div>
      </div>

      {/* Sort row -- same sortRow/sortGroup treatment as LibraryPage's own. No page-size
          picker here (removed per-user request 2026-08-31) -- infinite scroll alone decides
          how much is loaded. */}
      <div className={styles.sortRow}>
        <div className={styles.sortGroup}>
          <span className={styles.rowLabel}>Sort</span>
          <select
            className={styles.select}
            value={prefs.sort}
            onChange={e => { setPrefs({ sort: e.target.value as SortOption }); setJumpTarget(null) }}
          >
            {SORT_OPTIONS.map(o => (
              <option key={o.value} value={o.value}>{o.label}</option>
            ))}
          </select>
        </div>
      </div>

      {roles.length > 0 && (
        <div className={styles.roleChips}>
          {visibleRoles.map(role => (
            <button
              key={role}
              type="button"
              className={prefs.role === role ? styles.roleChipActive : styles.roleChip}
              onClick={() => toggleRole(role)}
            >
              {role}
            </button>
          ))}
          {roles.length > VISIBLE_ROLE_COUNT && (
            <button
              type="button"
              className={styles.roleChipToggle}
              onClick={() => setRolesExpanded(e => !e)}
            >
              {rolesExpanded ? 'Show fewer' : `Show all ${roles.length} roles`}
            </button>
          )}
        </div>
      )}

      {isInitialLoading ? (
        <div className={styles.empty}>Loading…</div>
      ) : people.length === 0 ? (
        <div className={styles.empty}>No people found.</div>
      ) : (
        <div ref={gridRef} style={{ position: 'relative', height: virtualizer.getTotalSize() }}>
          {virtualRows.map(virtualRow => {
            const rowStart = virtualRow.index * columnsPerRow
            const rowPeople = people.slice(rowStart, rowStart + columnsPerRow)
            return (
              <div
                key={virtualRow.key}
                style={{
                  position: 'absolute',
                  top: 0,
                  left: 0,
                  width: '100%',
                  transform: `translateY(${virtualRow.start}px)`,
                  display: 'grid',
                  gridTemplateColumns: stretchMode
                    ? `repeat(${columnsPerRow}, 1fr)`
                    : `repeat(${columnsPerRow}, ${cardWidth}px)`,
                  gap: GRID_GAP,
                }}
              >
                {rowPeople.map(person => (
                  <PersonCard key={person.id} person={person} navState={peopleNavState} />
                ))}
              </div>
            )
          })}
        </div>
      )}

      {isFetchingNextPage && <div className={styles.loadingMore}>Loading more…</div>}
      {!hasNextPage && people.length > 0 && (
        <div className={styles.endOfList}>— end of list —</div>
      )}

      {prefs.sort === 'name' && (
        <AlphabetRail activeLetter={jumpTarget} onJump={jumpToLetter} />
      )}
    </div>
  )
}
