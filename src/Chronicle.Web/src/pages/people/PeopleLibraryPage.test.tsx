import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import PeopleLibraryPage from './PeopleLibraryPage'
import { renderPeoplePage, stubViewportGeometry } from '@/test/people-test-utils'
import type { PersonListItem } from '@/types'
import * as peopleApi from '@/api/people'
import { logPeopleDebug } from '@/utils/peopleDebugLog'

vi.mock('@/api/people')
vi.mock('@/utils/peopleDebugLog', () => ({ logPeopleDebug: vi.fn() }))

const mockedGetPeople = vi.mocked(peopleApi.getPeople)
const mockedGetJumpPosition = vi.mocked(peopleApi.getJumpPosition)
const mockedGetPersonRoles = vi.mocked(peopleApi.getPersonRoles)
const mockedLogPeopleDebug = vi.mocked(logPeopleDebug)

const PAGE_SIZE = 40
const TOTAL_PEOPLE = 1200 // 30 pages -- large enough that a jump target lands well past page 1

function makePerson(index: number): PersonListItem {
  // Zero-padded so string sort order matches numeric order, which the tests below rely on
  // when asserting "person N appears" for a specific absolute index.
  const n = String(index).padStart(4, '0')
  return {
    id: 100000 + index,
    name: `Person ${n}`,
    posterUrl: null,
    birthDate: '1950-01-01T00:00:00Z',
    deathDate: null,
    roles: ['Actor'],
  }
}

/** Serves getPeople exactly like the real GetPeople endpoint: page/perPage index into the FULL
 * ordered list regardless of where a jump opened the list (see PeopleLibraryPage's own top-of-
 * file doc comment on this). */
function installGetPeopleHandler() {
  mockedGetPeople.mockImplementation(async ({ page = 1, perPage = PAGE_SIZE } = {}) => {
    const start = (page - 1) * perPage
    const items = Array.from({ length: perPage }, (_, i) => makePerson(start + i))
      .filter(p => p.id - 100000 < TOTAL_PEOPLE)
    return { items, total: TOTAL_PEOPLE }
  })
}

beforeEach(() => {
  localStorage.clear()
  stubViewportGeometry()
  installGetPeopleHandler()
  mockedGetPersonRoles.mockResolvedValue(['Actor', 'Director'])
  mockedGetJumpPosition.mockResolvedValue({ index: 0, total: TOTAL_PEOPLE })

  // Faithful to jsdom's real behavior (it has no layout/paint pipeline, so scrollTo() is
  // unimplemented by default) AND to the specific defect this suite exists to guard against:
  // Element.scrollTo() updates scrollTop synchronously without dispatching a 'scroll' event --
  // confirmed live against a real browser (see PeopleLibraryPage.tsx's own comment on the fix)
  // as exactly why @tanstack/react-virtual's internal offset can get stuck. A stub that also
  // fired a synthetic scroll event here would silently paper over the bug this file tests for.
  Element.prototype.scrollTo = function scrollTo(this: HTMLElement, opts?: ScrollToOptions) {
    const top = opts?.top
    if (typeof top === 'number') Object.defineProperty(this, 'scrollTop', { configurable: true, value: top })
  } as typeof Element.prototype.scrollTo
})

afterEach(() => {
  vi.clearAllMocks()
})

describe('PeopleLibraryPage', () => {
  it('loads roles and the first page of people on a plain visit', async () => {
    renderPeoplePage(<PeopleLibraryPage />, { initialEntries: ['/people'] })

    expect(await screen.findByText('Person 0000')).toBeInTheDocument()
    expect(mockedGetPeople).toHaveBeenCalledWith(
      expect.objectContaining({ role: 'Actor', page: 1, perPage: PAGE_SIZE }),
    )
    // A plain visit never resolves a jump position -- confirms the two code paths
    // (jumpTarget == null vs a real jump) stay genuinely separate.
    expect(mockedGetJumpPosition).not.toHaveBeenCalled()
  })

  it('re-queries with the selected deceased filter', async () => {
    const user = userEvent.setup()
    renderPeoplePage(<PeopleLibraryPage />)
    await screen.findByText('Person 0000')

    await user.click(screen.getByRole('button', { name: 'Deceased' }))

    await waitFor(() => {
      expect(mockedGetPeople).toHaveBeenCalledWith(
        expect.objectContaining({ deceased: true, page: 1 }),
      )
    })
  })

  it('toggles a role filter chip on and off', async () => {
    const user = userEvent.setup()
    renderPeoplePage(<PeopleLibraryPage />)
    await screen.findByText('Person 0000')

    const directorChip = await screen.findByRole('button', { name: 'Director' })
    await user.click(directorChip)
    await waitFor(() => {
      expect(mockedGetPeople).toHaveBeenLastCalledWith(
        expect.objectContaining({ role: 'Director' }),
      )
    })

    // Clicking the same chip again clears the filter entirely (toggleRole's own semantics).
    await user.click(directorChip)
    await waitFor(() => {
      expect(mockedGetPeople).toHaveBeenLastCalledWith(
        expect.objectContaining({ role: undefined }),
      )
    })
  })

  // --- Regression coverage for the 2026-09-06 "reload the people list and it gives me a
  // blank screen" fix -------------------------------------------------------------------

  it('renders the jumped-to person, not a blank screen, when a jump opens deep in the list', async () => {
    // Index 960 -> page 25 (960 / 40 + 1), exactly mirroring the live repro that found this bug
    // (a jump landing well past the first page, before any of pages 1-24 have ever loaded).
    const targetIndex = 960
    mockedGetJumpPosition.mockResolvedValue({ index: targetIndex, total: TOTAL_PEOPLE })

    renderPeoplePage(<PeopleLibraryPage />, {
      initialEntries: [{ pathname: '/people', state: { jumpTo: 'Person 0960' } }],
    })

    // Without the fix, this never resolves: @tanstack/react-virtual's scrollOffset stays stuck
    // at 0 after scrollToIndex() (since Element.scrollTo() here deliberately does not dispatch a
    // 'scroll' event, matching the real defect), so every row keeps rendering for the TOP of the
    // list and Person 0960 is never among them.
    expect(await screen.findByText('Person 0960', {}, { timeout: 3000 })).toBeInTheDocument()
  })

  it('does not permanently disable auto-load after resubmitting the same jump target twice', async () => {
    const user = userEvent.setup()
    const targetIndex = 400
    mockedGetJumpPosition.mockResolvedValue({ index: targetIndex, total: TOTAL_PEOPLE })

    const { main } = renderPeoplePage(<PeopleLibraryPage />, { initialEntries: ['/people'] })

    const jumpInput = screen.getByPlaceholderText('Jump to name…')
    await user.type(jumpInput, 'Person 0400')
    await user.click(screen.getByRole('button', { name: 'Jump' }))
    await screen.findByText('Person 0400')

    const callsAfterFirstJump = mockedGetPeople.mock.calls.length

    // The exact regression this test guards: submitJumpSearch doesn't clear the input after a
    // successful jump, so a user clicking "Jump" again with unchanged text is a real, easy path
    // -- and was a same-value React state update that silently skipped re-rendering, permanently
    // wedging the auto-load effect's guard (see PeopleLibraryPage.tsx's jumpRequestId doc).
    await user.click(screen.getByRole('button', { name: 'Jump' }))

    // Simulate the user scrolling further into the list -- a real 'scroll' event, not a
    // programmatic scrollTo, so this exercises the auto-load effect the same way manual
    // scrolling does in production.
    Object.defineProperty(main, 'scrollTop', { configurable: true, value: main.scrollTop + 20000 })
    main.dispatchEvent(new Event('scroll'))

    await waitFor(() => {
      expect(mockedGetPeople.mock.calls.length).toBeGreaterThan(callsAfterFirstJump)
    })
  })

  it('lets a person past every jump target actually load once scrolled to (no infinite "Loading earlier…")', async () => {
    mockedGetJumpPosition.mockResolvedValue({ index: 40, total: TOTAL_PEOPLE }) // page 2
    const { main } = renderPeoplePage(<PeopleLibraryPage />, {
      initialEntries: [{ pathname: '/people', state: { jumpTo: 'Person 0040' } }],
    })

    await screen.findByText('Person 0040')
    expect(screen.queryByText('Loading earlier…')).not.toBeInTheDocument()

    // Scroll toward the front of the list -- page 1 should auto-load backward from here.
    Object.defineProperty(main, 'scrollTop', { configurable: true, value: 0 })
    main.dispatchEvent(new Event('scroll'))

    await waitFor(() => {
      expect(mockedGetPeople).toHaveBeenCalledWith(expect.objectContaining({ page: 1 }))
    })
  })

  it('re-scrolls to the jump target if columnsPerRow changes after the jump already scrolled', async () => {
    // Root-caused live (2026-09-07) via peopleDebugLog: columnsPerRow starts at MIN_COLUMNS (3)
    // on mount, since the ResizeObserver that measures the real column count only attaches once
    // the grid element exists -- so a jump's first scrollToIndex often runs against a WRONG,
    // placeholder columnsPerRow. Once the observer corrects it moments later (e.g. 3 -> 6 on a
    // normal desktop width), the same pixel scrollTop suddenly means a completely different
    // absolute item -- confirmed live as a jump to a "B" name drifting toward the "C"s with no
    // user interaction. This reproduces that exact sequence: a jump scrolls once under the
    // placeholder column count, then the observer reports a wider layout, and asserts a SECOND
    // scroll happens to the recomputed (different) position instead of leaving the stale one.
    const targetIndex = 400
    mockedGetJumpPosition.mockResolvedValue({ index: targetIndex, total: TOTAL_PEOPLE })

    const resizeCallbackBox: { current: ResizeObserverCallback | null } = { current: null }
    const OriginalResizeObserver = globalThis.ResizeObserver
    class CapturingResizeObserver {
      constructor(cb: ResizeObserverCallback) { resizeCallbackBox.current = cb }
      observe() { /* no-op -- this test drives the callback manually */ }
      unobserve() {}
      disconnect() {}
    }
    globalThis.ResizeObserver = CapturingResizeObserver as unknown as typeof ResizeObserver

    // Counts OUR OWN "we decided to scroll to this row" log calls, not raw DOM scrollTo()
    // invocations -- @tanstack/react-virtual's scrollToIndex can itself issue more than one
    // underlying scrollTo() while it converges on a measured (rather than estimated) row
    // position, which would make a raw DOM-level call count an unreliable signal for whether
    // OUR effect actually decided to re-scroll.
    function jumpScrollToIndexCalls() {
      return mockedLogPeopleDebug.mock.calls
        .filter(call => call[0] === 'jump-scroll-to-index')
        .map(call => call[1]?.rowIndex)
    }

    try {
      renderPeoplePage(<PeopleLibraryPage />, {
        initialEntries: [{ pathname: '/people', state: { jumpTo: 'Person 0400' } }],
      })

      await screen.findByText('Person 0400')
      expect(jumpScrollToIndexCalls()).toEqual([133]) // floor(400 / MIN_COLUMNS=3)

      // Simulate the ResizeObserver correcting columnsPerRow from MIN_COLUMNS (3) to a wider
      // desktop layout (6) after the grid already mounted and the jump already scrolled once.
      resizeCallbackBox.current?.(
        [{ contentRect: { width: 1200 } } as ResizeObserverEntry],
        {} as ResizeObserver,
      )

      await waitFor(() => {
        expect(jumpScrollToIndexCalls()).toEqual([133, 66]) // re-scrolled using floor(400 / 6)
      })
    } finally {
      globalThis.ResizeObserver = OriginalResizeObserver
    }
  })

  it('still auto-loads the next page via the scroll-position backstop when no scroll event ever fires', async () => {
    // Root-caused live (2026-09-07): a user reported infinite-scroll going permanently silent
    // while genuinely scrolled to the bottom of what was loaded, and it started working again
    // the instant they opened DevTools -- the fingerprint of a browser occasionally not
    // delivering a 'scroll' event to @tanstack/react-virtual's own listener, the same failure
    // mode already confirmed for the jump-to-target path elsewhere in this suite, just hitting
    // ordinary scrolling too. This test reproduces exactly that: it moves the real scrollTop
    // WITHOUT ever dispatching a 'scroll' event (the primary, virtualRows-driven auto-load
    // effect has nothing to react to), and asserts the independent polling backstop still
    // notices and fetches the next page within one interval tick.
    const { main } = renderPeoplePage(<PeopleLibraryPage />, { initialEntries: ['/people'] })
    await screen.findByText('Person 0000')

    const callsBefore = mockedGetPeople.mock.calls.length

    // Real scrollTop moves; deliberately no main.dispatchEvent(new Event('scroll')) call.
    Object.defineProperty(main, 'scrollTop', { configurable: true, value: 20000 })

    await waitFor(() => {
      expect(mockedGetPeople.mock.calls.length).toBeGreaterThan(callsBefore)
    }, { timeout: 2000 })
  })

  // --- Regression coverage for the 2026-09-11 "infinite scroll stops for minutes, then
  // catches up the instant you look at the tab" fix -------------------------------------

  it.each(['visibilitychange', 'focus'] as const)(
    'immediately re-checks and fetches on a %s event, without waiting for the polling backstop',
    async eventName => {
      // Root-caused live (2026-09-11) via peopleDebugLog: the 500ms polling backstop above is a
      // plain setInterval, which browsers throttle almost to a stop on a backgrounded/unfocused
      // tab -- confirmed live as several silent multi-minute gaps with zero ticks logged, each
      // one ending in a burst of catch-up activity the instant the tab regained attention. The
      // fix re-runs the same check on 'visibilitychange'/'focus' so returning to the tab catches
      // it up right away instead of waiting on the very timer that just got throttled.
      //
      // setInterval is stubbed to a no-op here so this test can only pass via that new listener
      // -- without the stub, the real 500ms backstop tick could coincidentally fire first and
      // make this test pass for the wrong reason.
      const intervalSpy = vi.spyOn(window, 'setInterval')
        .mockReturnValue(0 as unknown as ReturnType<typeof setInterval>)

      const { main } = renderPeoplePage(<PeopleLibraryPage />, { initialEntries: ['/people'] })
      await screen.findByText('Person 0000')

      const callsBefore = mockedGetPeople.mock.calls.length

      // Real scrollTop moves; no 'scroll' event dispatched, and the backstop's own interval
      // never actually runs (stubbed above) -- the only remaining path that can notice this and
      // fetch is the new visibilitychange/focus listener.
      Object.defineProperty(main, 'scrollTop', { configurable: true, value: 20000 })
      const target = eventName === 'focus' ? window : document
      target.dispatchEvent(new Event(eventName))

      await waitFor(() => {
        expect(mockedGetPeople.mock.calls.length).toBeGreaterThan(callsBefore)
      })

      intervalSpy.mockRestore()
    },
  )

  it('does not fetch on a visibilitychange event while the tab is going hidden, not becoming visible', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval')
      .mockReturnValue(0 as unknown as ReturnType<typeof setInterval>)
    const visibilitySpy = vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('hidden')

    const { main } = renderPeoplePage(<PeopleLibraryPage />, { initialEntries: ['/people'] })
    await screen.findByText('Person 0000')

    const callsBefore = mockedGetPeople.mock.calls.length
    Object.defineProperty(main, 'scrollTop', { configurable: true, value: 20000 })
    document.dispatchEvent(new Event('visibilitychange'))

    // No waitFor here on purpose -- asserting a negative needs to check the settled state, not
    // race a timeout against work that (correctly) never happens.
    await Promise.resolve()
    expect(mockedGetPeople.mock.calls.length).toBe(callsBefore)

    visibilitySpy.mockRestore()
    intervalSpy.mockRestore()
  })
})
