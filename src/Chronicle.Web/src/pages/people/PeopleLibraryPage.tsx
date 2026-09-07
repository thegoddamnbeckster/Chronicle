import { useCallback, useContext, useEffect, useRef, useState } from 'react'
import { useLocation } from 'react-router-dom'
import { useQuery, useInfiniteQuery } from '@tanstack/react-query'
import { useVirtualizer } from '@tanstack/react-virtual'
import { getPeople, getPersonRoles, getJumpPosition } from '@/api/people'
import type { PersonListItem } from '@/types'
import { PersonCard } from '@/components/people/PersonCard'
import { AlphabetRail } from '@/components/people/AlphabetRail'
import { MainScrollContext } from '@/components/layout/Layout'
import {
  loadPeoplePrefs, savePeoplePrefs, type PeopleLibraryPrefs,
} from '@/utils/peopleLibraryPrefs'
import styles from './PeopleLibraryPage.module.css'

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
 * person, so this mirrors that page's general shape only -- except for filters/page-size
 * controls, which now deliberately DO mirror LibraryPage's own (utils/peopleLibraryPrefs.ts),
 * per-user request (2026-08-30): "I need the same kinds of controls that the library has.
 * similar filtering and the ability to save."
 *
 * Always sorted alphabetically by last name (PersonNameHelper.ToLastNameFirstSortKey,
 * PeopleController.GetPeople) -- per-user request (2026-08-31): "delete the sorting and keep
 * it alphabetical by last name." There used to be a Name/Birth Date/Recently Added picker;
 * removed rather than kept alongside the one fixed order.
 *
 * Virtualized (only on-screen + a small overscan buffer ever exist as real DOM nodes) and
 * jump-to-letter (an A-Z rail plus a jump-search box) per a further request the same day:
 * "let whatever will let my phone scroll through the people as I want to scroll and will
 * let me jump into a mid point of the list of people as I need to without having to reload
 * the entire list." New pages auto-load as the virtualized window approaches either end of
 * what's already fetched -- no manual "Load More" click needed any more, in either direction.
 *
 * A jump resolves via PeopleController.GetJumpPosition to a 0-based index into the FULL
 * ordered list, then opens the infinite-scroll list on whichever page that index falls on --
 * both earlier and later pages load from there via ordinary forward/backward auto-load, same
 * as a plain (non-jump) visit. Per-user request (2026-09-03): "what is the possibility of
 * having the ability to scroll up when you click up people from a person's detail page...
 * it just sticks that person at the top." GetPeople itself used to truncate the ordered list
 * down to "target and everything after" on a jump, so nothing before the jumped-to person was
 * ever loaded and scrolling up literally had nothing to scroll into -- see GetJumpPosition's
 * own doc for the full story. Page numbers now always index into the FULL list (page 1 is
 * always the true start of the alphabet); a jump only changes which page the list *opens* on. */
export default function PeopleLibraryPage() {
  const location = useLocation()
  const [prefs, setPrefsState] = useState<PeopleLibraryPrefs>(loadPeoplePrefs)
  const [rolesExpanded, setRolesExpanded] = useState(false)
  // Ephemeral, not persisted -- a jump is a "go here now" action, not a sticky preference;
  // reloading the page should land wherever the persisted sort/filter/page-size normally
  // puts you, not stuck mid-jump from last time. Initialized from PersonDetailPage's "↑
  // People" link (state: { jumpTo }), when present, so returning from a person's page lands
  // back near them instead of at the top of the list -- same idea as MediaDetailPage's own
  // "↑ Library" anchor, just via this page's jump mechanism instead of a URL hash (this grid
  // is virtualized, so an off-screen item isn't in the DOM for the browser to scroll to).
  const [jumpTarget, setJumpTarget] = useState<string | null>(
    () => (location.state as { jumpTo?: string } | null)?.jumpTo ?? null)
  const [jumpInput, setJumpInput] = useState('')
  // Identifies a single jump ACTION, separately from jumpTarget's own value -- incremented on
  // every jump, even a re-jump to the identical letter/name. Needed because jumpTarget alone
  // can't reliably signal "a new jump happened": React bails out of re-rendering (and therefore
  // re-running effects) on a same-value useState update, so re-submitting the SAME text in the
  // jump-search form (submitJumpSearch doesn't clear jumpInput after a successful jump, so
  // clicking "Jump" again with unchanged text is a real, easy-to-hit path) would otherwise never
  // trigger a new render at all. Confirmed live (2026-09-06) as a real regression this caused:
  // scrolledForJumpRef.current got reset to null with no following render to ever set it back,
  // permanently stuck-blocking the auto-load effect's guard below for the rest of that jump.
  const [jumpRequestId, setJumpRequestId] = useState(0)
  // Tracks which jumpRequestId the virtualizer has already been scrolled to, so the scroll-to-
  // target effect (below) fires exactly once per jump instead of re-scrolling on every
  // subsequent page load/re-render while that jump is still active. Compared against
  // jumpRequestId rather than jumpTarget precisely so a same-target re-jump (which bumps the id
  // even though the target string is unchanged) is never confused with "already handled."
  const scrolledForJumpRef = useRef<number | null>(null)

  function setPrefs(updates: Partial<PeopleLibraryPrefs>) {
    const next = { ...prefs, ...updates }
    setPrefsState(next)
    savePeoplePrefs(next) // synchronous, on every change -- same as LibraryPage's own setPrefs
  }

  function jumpToLetter(letter: string) {
    scrolledForJumpRef.current = null
    setJumpTarget(letter)
    setJumpRequestId(id => id + 1)
    setJumpInput('')
  }

  function submitJumpSearch(e: React.FormEvent) {
    e.preventDefault()
    if (jumpInput.trim()) {
      scrolledForJumpRef.current = null
      setJumpTarget(jumpInput.trim())
      setJumpRequestId(id => id + 1)
    }
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

  const deceasedParam = prefs.deceased === 'either' ? undefined : prefs.deceased === 'deceased'

  // Resolves jumpTarget to a 0-based index into the FULL ordered list (see GetJumpPosition's
  // own doc) -- only runs for an actual jump; a plain visit skips straight to the bidirectional
  // query below with its default initialPageParam of 1.
  const jumpPositionQuery = useQuery({
    queryKey: ['people-jump-position', jumpTarget, primaryRole, deceasedParam],
    queryFn: () => getJumpPosition({ jumpTo: jumpTarget!, role: primaryRole, deceased: deceasedParam }),
    enabled: jumpTarget != null,
  })

  // Which GetPeople page the list should OPEN on: page 1 for a plain visit, or whichever page
  // the jump target's index falls on once jumpPositionQuery resolves. Null while a jump is
  // still resolving -- gates the query below via `enabled` rather than guessing a wrong
  // starting page and having to correct it after the fact.
  const initialPage = jumpTarget == null
    ? 1
    : jumpPositionQuery.data
      ? Math.floor(jumpPositionQuery.data.index / PEOPLE_PAGE_SIZE) + 1
      : null

  // Bidirectional server-side pagination: pages are keyed by their own absolute page number
  // (GetPeople's page/perPage always index into the FULL list now, jump or not), so
  // fetchNextPage/fetchPreviousPage extend the loaded window in either direction from
  // wherever it opened without ever needing to shift already-rendered rows -- see the
  // absolute-index item lookup below.
  const peopleQuery = useInfiniteQuery({
    queryKey: ['people', primaryRole, deceasedParam, jumpTarget],
    queryFn: ({ pageParam }) => getPeople({
      role: primaryRole, deceased: deceasedParam, page: pageParam, perPage: PEOPLE_PAGE_SIZE,
    }),
    initialPageParam: initialPage ?? 1,
    enabled: initialPage != null,
    getNextPageParam: (lastPage, _allPages, lastPageParam) => {
      const maxPage = Math.max(1, Math.ceil((lastPage.total ?? 0) / PEOPLE_PAGE_SIZE))
      return lastPageParam < maxPage ? lastPageParam + 1 : undefined
    },
    getPreviousPageParam: (_firstPage, _allPages, firstPageParam) =>
      firstPageParam > 1 ? firstPageParam - 1 : undefined,
  })

  const total = peopleQuery.data?.pages[0]?.total ?? jumpPositionQuery.data?.total ?? 0

  // Loaded items placed at their true absolute position in the full list -- gaps (pages not
  // yet fetched, above or below whatever's currently loaded) are simply absent from the map
  // rather than shifting anything, which is what lets fetchPreviousPage prepend without any
  // scroll-position correction: a row's absolute index never changes once assigned.
  const itemsByIndex = new Map<number, PersonListItem>()
  const pageParams = (peopleQuery.data?.pageParams ?? []) as number[]
  peopleQuery.data?.pages.forEach((page, i) => {
    const pageNum = pageParams[i]
    page.items.forEach((item, j) => itemsByIndex.set((pageNum - 1) * PEOPLE_PAGE_SIZE + j, item))
  })

  const isInitialLoading = jumpTarget != null
    ? (jumpPositionQuery.isLoading || (initialPage != null && peopleQuery.data === undefined))
    : peopleQuery.data === undefined
  const hasAnyLoaded = itemsByIndex.size > 0

  // Carried as router state into PersonDetailPage so its "↑ People" / Prev / Next nav
  // (mirrors MediaDetailPage's own "↑ Library" + list nav) knows the list currently loaded
  // here, same idea as LibraryPage's per-section listIds/listLabel. Only the densely-loaded
  // (no gaps) window, in true list order -- exactly what was already fetched, same as before.
  const loadedIndices = Array.from(itemsByIndex.keys()).sort((a, b) => a - b)
  const peopleNavState = {
    listIds: loadedIndices.map(i => itemsByIndex.get(i)!.id),
    listLabel: 'People',
  }

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
    // hasAnyLoaded: the grid <div ref={gridRef}> only exists once there's something to show
    // it (it's behind the isInitialLoading/empty conditional below) -- an effect with []
    // deps would run once on mount, find gridRef.current still null at that point, and
    // silently never attach the observer at all once the div actually appears. Re-running
    // this effect whenever that flips true (first load, OR a filter change that goes from
    // zero results to some) is what lets it find the real element.
  }, [hasAnyLoaded])

  const rowHeight = cardWidth * 1.5 + CARD_INFO_HEIGHT
  const rowCount = Math.ceil(total / columnsPerRow)

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

  // Auto-load the next OR previous page as the virtualized window approaches either end of
  // what's ALREADY BEEN FETCHED -- not the end of the full dataset. Confirmed live (2026-09-05)
  // as the actual cause of "infinite scroll stops populating tiles partway down": this used to
  // compare against `rowCount` (derived from `total`, the FULL list size), so for any library
  // bigger than one page -- e.g. 28,629 people at 40/page is ~9,500 rows -- the trigger
  // (`lastRow.index >= rowCount - 3`) only ever fires within the last few rows of the ENTIRE
  // list, which normal scrolling never reaches. Page 1 loads, and everything past its own last
  // row renders as permanently blank placeholder cards because fetchNextPage() is never called.
  // Comparing against the highest/lowest currently LOADED page (pageParams, already tracked by
  // useInfiniteQuery) instead of the dataset total is what actually fixes it. Per-user request
  // (2026-08-30, extended 2026-09-03 to cover backward loading too): "automatically loads new
  // items as I need them." No manual "Load More" click in either direction.
  useEffect(() => {
    const firstRow = virtualRows[0]
    const lastRow = virtualRows[virtualRows.length - 1]
    if (!firstRow || !lastRow) return
    // While a jump's own initial scroll hasn't happened yet, virtualRows still reflect wherever
    // the viewport was BEFORE the jump (usually row 0) -- comparing that against the page range
    // the jump opened on (which can be far away) would walk every page back toward 1 before the
    // real scroll position is ever known. Confirmed live (2026-09-06) as a contributor to the
    // "reload the people list and it gives me a blank screen" bug: this effect runs in the same
    // render pass as the scroll-to-jump effect below but is declared first, so it always sees
    // one stale round of virtualRows on a jump's first render. The scroll-to-jump effect flips
    // scrolledForJumpRef the moment it actually moves the viewport, which re-runs THIS effect too
    // -- via virtualRows changing as a side effect of that scroll (already a dep below), not via
    // itemsByIndex.size, which isn't one. Compared against jumpRequestId, not jumpTarget: a
    // same-target re-jump changes only the id (see jumpRequestId's own doc), so gating on the
    // target string alone would miss it and stay stuck permanently true for that case.
    if (jumpTarget != null && scrolledForJumpRef.current !== jumpRequestId) return

    const maxLoadedPage = pageParams.length > 0 ? Math.max(...pageParams) : (initialPage ?? 1)
    const minLoadedPage = pageParams.length > 0 ? Math.min(...pageParams) : (initialPage ?? 1)
    const maxLoadedRow = Math.floor((maxLoadedPage * PEOPLE_PAGE_SIZE - 1) / columnsPerRow)
    const minLoadedRow = Math.floor(((minLoadedPage - 1) * PEOPLE_PAGE_SIZE) / columnsPerRow)

    if (lastRow.index >= maxLoadedRow - 3 && peopleQuery.hasNextPage && !peopleQuery.isFetchingNextPage) {
      peopleQuery.fetchNextPage()
    }
    if (firstRow.index <= minLoadedRow + 2 && peopleQuery.hasPreviousPage && !peopleQuery.isFetchingPreviousPage) {
      peopleQuery.fetchPreviousPage()
    }
  }, [
    virtualRows, columnsPerRow, pageParams, initialPage, jumpTarget, jumpRequestId,
    peopleQuery.hasNextPage, peopleQuery.isFetchingNextPage, peopleQuery.fetchNextPage,
    peopleQuery.hasPreviousPage, peopleQuery.isFetchingPreviousPage, peopleQuery.fetchPreviousPage,
  ])

  // Scrolls the virtualized window to the jumped-to person's row exactly once per jump --
  // the whole point of GetJumpPosition (see its own doc): without this, the list would open
  // on the right PAGE but still visually start scrolled to wherever the viewport already was.
  // Waits for the initial page to actually be loaded (itemsByIndex has the jump target's row)
  // so there's real content to scroll to, not just an empty virtualized placeholder area.
  useEffect(() => {
    if (jumpTarget == null || !jumpPositionQuery.data) return
    if (scrolledForJumpRef.current === jumpRequestId) return
    if (!itemsByIndex.has(jumpPositionQuery.data.index) && jumpPositionQuery.data.index < total) return
    // Clamp to the last real item: a jump target that sorts past everyone (e.g. "Zzz" in a
    // catalog with no matching last name) resolves to index === total, which is one past the
    // last valid absolute index. Caught in code review (2026-09-03): when total happens to be
    // an exact multiple of columnsPerRow, Math.floor(total / columnsPerRow) equals rowCount
    // itself -- one row past the last one that exists -- so scrollToIndex needs the clamped
    // index, not the raw (possibly out-of-bounds) one from GetJumpPosition.
    const clampedIndex = Math.min(jumpPositionQuery.data.index, Math.max(total - 1, 0))
    const rowIndex = Math.floor(clampedIndex / columnsPerRow)
    virtualizer.scrollToIndex(rowIndex, { align: 'start' })
    scrolledForJumpRef.current = jumpRequestId
    // Root-caused live (2026-09-06) as the actual cause of "reload the people list and it gives
    // me a blank screen": a jump this deep opens the page already scrolled hundreds of rows down
    // (via scrollToIndex above), but @tanstack/react-virtual only learns the resulting scrollTop
    // from the browser's own 'scroll' event -- confirmed via direct virtualizer inspection, that
    // event does not reliably arrive for a programmatic scrollTo this early in the page's life.
    // Without it, virtualizer.scrollOffset is stuck at its initial 0 forever: every row keeps
    // rendering for the TOP of the list while the real scrollTop sits deep below them, so the
    // visible viewport shows nothing -- and the auto-load effect above, which trusts those same
    // rows, reads that as "still near the start" and walks every earlier page back to 1 fetching
    // content nobody's looking at. scrollTo() with the default (non-smooth) behavior updates
    // Element.scrollTop synchronously -- confirmed live, immediately after scrollToIndex returns
    // -- so a synthetic 'scroll' dispatch recovers it regardless of why the native event didn't
    // arrive. Deferred via setTimeout rather than fired inline: @tanstack/react-virtual's own
    // scroll handler calls React's flushSync to stay perceptually instant during real scrolling,
    // and flushSync throws if triggered synchronously from inside this effect (React is still
    // mid-commit) -- confirmed live (2026-09-06) as its own "flushSync was called from inside a
    // lifecycle method" console error. A macrotask escapes that window; unlike rAF, it isn't
    // paused indefinitely for a backgrounded tab. A real subsequent scroll event, if one does
    // land before this fires, just makes it a harmless no-op re-read of the same value.
    // Deliberately not cancelled on cleanup: <main> (MainScrollContext, owned by Layout.tsx)
    // outlives this component across route changes, so a timeout that still fires after a real
    // unmount just dispatches a harmless extra 'scroll' re-read on a still-valid element -- but a
    // returned `clearTimeout` would ALSO fire on React StrictMode's dev-only simulated
    // mount-cleanup-remount, cancelling this run's timer with no later run to replace it (the
    // guard above already blocks re-entry once scrolledForJumpRef is set), silently reintroducing
    // the exact bug this effect exists to fix, in dev only.
    setTimeout(() => {
      mainScrollRef.current?.dispatchEvent(new Event('scroll'))
    }, 0)
    // itemsByIndex.size (not the map itself, a new object every render) is what actually
    // needs to trigger a re-check here: a fresh page landing is the only thing that can flip
    // the has()/index<total guard above from false to true after the initial bail-out.
  }, [
    jumpTarget, jumpRequestId, jumpPositionQuery.data, itemsByIndex.size, total, columnsPerRow,
    mainScrollRef,
  ])

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h1>People</h1>
      </div>

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
      ) : total === 0 ? (
        <div className={styles.empty}>No people found.</div>
      ) : (
        <div ref={gridRef} style={{ position: 'relative', height: virtualizer.getTotalSize() }}>
          {virtualRows.map(virtualRow => {
            const rowStart = virtualRow.index * columnsPerRow
            // A row this far from what's loaded so far is a gap -- the auto-load effect above
            // will fetch the page it belongs to; until then it renders as empty space rather
            // than a card, same idea as the row itself being absolutely positioned.
            const rowPeople = Array.from({ length: columnsPerRow }, (_, col) => itemsByIndex.get(rowStart + col))
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
                {rowPeople.map((person, col) =>
                  person
                    ? <PersonCard key={person.id} person={person} navState={peopleNavState} />
                    : <div key={`empty-${rowStart + col}`} />
                )}
              </div>
            )
          })}
        </div>
      )}

      {peopleQuery.isFetchingPreviousPage && <div className={styles.loadingMore}>Loading earlier…</div>}
      {peopleQuery.isFetchingNextPage && <div className={styles.loadingMore}>Loading more…</div>}
      {!peopleQuery.hasNextPage && total > 0 && (
        <div className={styles.endOfList}>— end of list —</div>
      )}

      <AlphabetRail activeLetter={jumpTarget} onJump={jumpToLetter} />
    </div>
  )
}
