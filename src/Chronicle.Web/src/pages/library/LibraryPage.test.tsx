import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import LibraryPage from './LibraryPage'
import { renderWithProviders } from '@/test/test-utils'
import type { LibraryEntry, LibraryStatus, MediaItem } from '@/types'
import * as libraryApi from '@/api/library'
import * as mediaApi from '@/api/media'
import * as rightsApi from '@/api/mediaRights'
import { makeUser } from '@/test/users'

vi.mock('@/api/library')
vi.mock('@/api/media')
vi.mock('@/api/mediaRights')

// Counts how many times each poster renders, to prove that an unrelated change does not re-render every card on the page.
const posterRenders = vi.hoisted(() => ({} as Record<string, number>))
vi.mock('@/components/PosterImage', () => ({
  PosterImage: ({ name }: { name: string }) => {
    posterRenders[name] = (posterRenders[name] ?? 0) + 1
    return <div data-testid="poster" data-name={name} />
  },
}))

const mockedGetSections = vi.mocked(libraryApi.getLibrarySections)
const mockedGetSectionPage = vi.mocked(libraryApi.getLibrarySectionPage)
const mockedGetEntryForMedia = vi.mocked(libraryApi.getLibraryEntryForMedia)
const mockedUpdateLibraryEntry = vi.mocked(libraryApi.updateLibraryEntry)
const mockedRemoveFromLibrary = vi.mocked(libraryApi.removeFromLibrary)
const mockedDeleteMedia = vi.mocked(mediaApi.deleteMedia)

const PREFS_KEY = 'chronicle_library_prefs'

let nextId = 1

function makeMediaItem(overrides: Partial<MediaItem> = {}): MediaItem {
  const id = overrides.id ?? nextId++
  return {
    id,
    mediaTypeId: 1,
    mediaTypeName: 'Movies',
    parentId: null,
    name: `Movie ${id}`,
    year: 2000,
    overview: null,
    posterUrl: null,
    runtimeMinutes: null,
    hierarchyLevel: 0,
    number: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    externalIds: [],
    ...overrides,
  }
}

function makeEntry(overrides: Partial<LibraryEntry> = {}, mediaOverrides: Partial<MediaItem> = {}): LibraryEntry {
  const id = overrides.id ?? nextId++
  return {
    id,
    userId: 1,
    mediaItem: makeMediaItem({ id, ...mediaOverrides }),
    status: 'Unwatched',
    userRating: null,
    userRatingSource: null,
    notes: null,
    addedAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    startedAt: null,
    completedAt: null,
    resumePositionPercent: null,
    lastKnownProgressPercent: null,
    ...overrides,
  }
}

/** Serves the library the way the real endpoints do: section headers with counts, then one
 * sorted, status-filtered page of one section at a time. */
function installLibraryHandlers(entries: LibraryEntry[]) {
  const typeNames = [...new Set(entries.map(e => e.mediaItem.mediaTypeName))]
  const typeId = (name: string) => typeNames.indexOf(name) + 1
  const matching = (status?: LibraryStatus) => entries.filter(e => !status || e.status === status)

  mockedGetSections.mockImplementation(async (status?: LibraryStatus) =>
    typeNames
      .map(name => ({
        mediaTypeId: typeId(name),
        name,
        count: matching(status).filter(e => e.mediaItem.mediaTypeName === name).length,
      }))
      .filter(sec => sec.count > 0))

  mockedGetSectionPage.mockImplementation(async p => {
    const rows = matching(p.status)
      .filter(e => typeId(e.mediaItem.mediaTypeName) === p.mediaTypeId)
      .sort((a, b) => a.mediaItem.name.localeCompare(b.mediaItem.name))
    if (p.sortDir === 'desc') rows.reverse()
    return p.perPage > 0 ? rows.slice((p.page - 1) * p.perPage, p.page * p.perPage) : rows
  })
}

beforeEach(() => {
  localStorage.clear()
  nextId = 1
  mockedUpdateLibraryEntry.mockResolvedValue(makeEntry())
  mockedRemoveFromLibrary.mockResolvedValue(undefined)
  mockedDeleteMedia.mockResolvedValue(undefined)
  vi.spyOn(window, 'confirm').mockReturnValue(true)
})

afterEach(() => {
  vi.clearAllMocks()
  vi.restoreAllMocks()
})

describe('LibraryPage', () => {
  it('loads the library on a plain visit and renders items grouped by media type', async () => {
    installLibraryHandlers([
      makeEntry({ status: 'Watching' }, { name: 'Alpha Movie', mediaTypeName: 'Movies' }),
      makeEntry({ status: 'Completed' }, { name: 'Beta Show', mediaTypeName: 'TV Shows' }),
    ])

    renderWithProviders(<LibraryPage />)

    expect(await screen.findByText('Alpha Movie')).toBeInTheDocument()
    expect(screen.getByText('Beta Show')).toBeInTheDocument()
    expect(screen.getByText('Movies')).toBeInTheDocument()
    expect(screen.getByText('TV Shows')).toBeInTheDocument()

    // Since groupMoviesIntoCollections defaults to true, includeMoviesInCollections must be false.
    expect(mockedGetSections).toHaveBeenCalledWith(undefined, false)
    expect(mockedGetSectionPage).toHaveBeenCalledWith(expect.objectContaining({
      includeMoviesInCollections: false, page: 1, perPage: 24, sortBy: 'name', sortDir: 'asc',
    }))
  })

  it('fetches only the first page of a section, not the whole catalog', async () => {
    installLibraryHandlers(Array.from({ length: 60 }, (_, i) =>
      makeEntry({}, { name: `Film ${String(i).padStart(2, '0')}` })))

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Film 00')

    expect(screen.getByText('Film 23')).toBeInTheDocument()
    expect(screen.queryByText('Film 24')).not.toBeInTheDocument()
    // The header still shows the real total, from the sections call.
    expect(screen.getByText('60')).toBeInTheDocument()
    expect(mockedGetSectionPage).toHaveBeenCalledTimes(1)
    expect(mockedGetSectionPage).toHaveBeenCalledWith(expect.objectContaining({ page: 1, perPage: 24 }))
  })

  it('asks for the next page from the server when paging forward', async () => {
    const user = userEvent.setup()
    installLibraryHandlers(Array.from({ length: 60 }, (_, i) =>
      makeEntry({}, { name: `Film ${String(i).padStart(2, '0')}` })))

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Film 00')
    await user.click(screen.getByRole('button', { name: /Next/ }))

    expect(await screen.findByText('Film 24')).toBeInTheDocument()
    expect(mockedGetSectionPage).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2, perPage: 24 }))
  })

  it('does not fetch a collapsed section until it is opened', async () => {
    localStorage.setItem('chronicle.library.collapsed', JSON.stringify({ 'TV Shows': true }))
    installLibraryHandlers([
      makeEntry({}, { name: 'Alpha Movie', mediaTypeName: 'Movies' }),
      makeEntry({}, { name: 'Beta Show', mediaTypeName: 'TV Shows' }),
    ])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Alpha Movie')

    expect(screen.queryByText('Beta Show')).not.toBeInTheDocument()
    expect(mockedGetSectionPage).toHaveBeenCalledTimes(1)
    expect(mockedGetSectionPage).toHaveBeenCalledWith(expect.objectContaining({ mediaTypeId: 1 }))
  })

  it('filters by status on the server', async () => {
    const user = userEvent.setup()
    installLibraryHandlers([
      makeEntry({ status: 'Watching' }, { name: 'Watching Movie' }),
      makeEntry({ status: 'Completed' }, { name: 'Completed Movie' }),
    ])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Watching Movie')
    expect(screen.getByText('Completed Movie')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'In progress' }))

    await waitFor(() => {
      expect(screen.queryByText('Completed Movie')).not.toBeInTheDocument()
    })
    expect(screen.getByText('Watching Movie')).toBeInTheDocument()

    // The filter is part of the request, so counts and pages both reflect it.
    expect(mockedGetSections).toHaveBeenLastCalledWith('Watching', false)
    expect(mockedGetSectionPage).toHaveBeenLastCalledWith(expect.objectContaining({ status: 'Watching' }))
  })

  it('asks the server to re-sort when the sort order changes', async () => {
    const user = userEvent.setup()
    installLibraryHandlers([
      makeEntry({}, { name: 'Zebra' }),
      makeEntry({}, { name: 'Apple' }),
    ])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Zebra')

    // Default sort is name-asc: Apple before Zebra.
    let names = screen.getAllByText(/^(Apple|Zebra)$/).map(el => el.textContent)
    expect(names).toEqual(['Apple', 'Zebra'])

    await user.selectOptions(screen.getByDisplayValue('Name A–Z'), 'name-desc')

    await waitFor(() => {
      names = screen.getAllByText(/^(Apple|Zebra)$/).map(el => el.textContent)
      expect(names).toEqual(['Zebra', 'Apple'])
    })
    expect(mockedGetSectionPage).toHaveBeenLastCalledWith(expect.objectContaining({ sortBy: 'name', sortDir: 'desc' }))
  })

  it('persists preference changes to localStorage', async () => {
    const user = userEvent.setup()
    installLibraryHandlers([makeEntry({ status: 'Watching' }, { name: 'Some Movie' })])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Some Movie')

    await user.click(screen.getByRole('button', { name: 'In progress' }))

    await waitFor(() => {
      const stored = JSON.parse(localStorage.getItem(PREFS_KEY) ?? '{}')
      expect(stored.statusFilter).toBe('Watching')
    })
  })

  it('updates an entry status via the per-item select and calls the API with the right id/status', async () => {
    const user = userEvent.setup()
    const entry = makeEntry({ status: 'Unwatched' }, { name: 'Status Movie' })
    installLibraryHandlers([entry])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Status Movie')

    const statusSelect = screen.getByDisplayValue('Not started')
    await user.selectOptions(statusSelect, 'Completed')

    await waitFor(() => {
      expect(mockedUpdateLibraryEntry).toHaveBeenCalledWith(entry.id, { status: 'Completed' })
    })
  })

  it('removes an entry only after the user confirms the browser dialog', async () => {
    const user = userEvent.setup()
    const entry = makeEntry({}, { name: 'Removable Movie' })
    installLibraryHandlers([entry])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Removable Movie')

    // Decline the confirmation -- must NOT call the API.
    vi.mocked(window.confirm).mockReturnValueOnce(false)
    await user.click(screen.getByRole('button', { name: 'Remove' }))
    expect(mockedRemoveFromLibrary).not.toHaveBeenCalled()

    // Accept it -- must call removeFromLibrary with this entry's id.
    vi.mocked(window.confirm).mockReturnValueOnce(true)
    await user.click(screen.getByRole('button', { name: 'Remove' }))
    await waitFor(() => {
      expect(mockedRemoveFromLibrary).toHaveBeenCalledWith(entry.id)
    })
  })

  it('shows an ordinary user no bulk Delete once an administrator has reserved deletion', async () => {
    const user = userEvent.setup()
    vi.mocked(rightsApi.getMediaRights).mockResolvedValue({ deleteAdminOnly: true })
    const entry1 = makeEntry({}, { name: 'Bulk One' })
    installLibraryHandlers([entry1])

    renderWithProviders(<LibraryPage />, { user: makeUser(false) })
    await screen.findByText('Bulk One')
    await user.click(screen.getByRole('button', { name: 'Select' }))

    await waitFor(() => expect(screen.queryByRole('button', { name: /^Delete \(/ })).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Select All' })).toBeInTheDocument()
  })

  it('deletes selected items in bulk through select mode and the confirmation modal', async () => {
    const user = userEvent.setup()
    const entry1 = makeEntry({}, { name: 'Bulk One' })
    const entry2 = makeEntry({}, { name: 'Bulk Two' })
    installLibraryHandlers([entry1, entry2])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Bulk One')

    await user.click(screen.getByRole('button', { name: 'Select' }))
    // In select mode, clicking a card (rather than a link) toggles its selection.
    await user.click(screen.getByText('Bulk One'))

    const deleteBtn = screen.getByRole('button', { name: /Delete \(1\)/ })
    await user.click(deleteBtn)

    // Confirmation modal appears; confirm it.
    expect(screen.getByText(/This cannot be undone/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Delete' }))

    await waitFor(() => {
      expect(mockedDeleteMedia).toHaveBeenCalledWith(entry1.mediaItem.id)
    })
    expect(mockedDeleteMedia).not.toHaveBeenCalledWith(entry2.mediaItem.id)
  })

  // --- Regression coverage: the "Missing" badge / cardStub styling must key off
  // hasMetadataOnly, NOT isStub. A watch-history import (e.g. from SIMKL/Trakt) is a real,
  // non-stub library entry that still has no physical file, and hasMetadataOnly is the only
  // field that correctly answers "is this missing a file" in every case. Mirrors the same
  // fix already made to CollectionMetadataBox.tsx's own "Not in Library" badge. -------------

  it('shows the "Missing" badge based on hasMetadataOnly, not isStub', async () => {
    const stubOnly = makeEntry({}, { name: 'Stub Not Missing', isStub: true, hasMetadataOnly: false })
    const trulyMissing = makeEntry({}, { name: 'Truly Missing', isStub: false, hasMetadataOnly: true })
    installLibraryHandlers([stubOnly, trulyMissing])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Stub Not Missing')
    await screen.findByText('Truly Missing')

    const stubCard = screen.getByText('Stub Not Missing').closest('div[id^="media-"]')!
    const missingCard = screen.getByText('Truly Missing').closest('div[id^="media-"]')!

    expect(within(stubCard as HTMLElement).queryByText('Missing')).not.toBeInTheDocument()
    expect(within(missingCard as HTMLElement).getByText('Missing')).toBeInTheDocument()
  })

  it('prefixes an episode/season entry with its show name via ancestors[0]', async () => {
    installLibraryHandlers([
      makeEntry({}, {
        name: 'S01E01',
        mediaTypeName: 'Episodes',
        ancestors: [{ id: 999, name: 'Parent Show' }],
      }),
    ])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('S01E01')
    expect(screen.getByText('Parent Show')).toBeInTheDocument()
  })

  it('shows an empty-library message when there are no entries', async () => {
    installLibraryHandlers([])
    renderWithProviders(<LibraryPage />)
    expect(await screen.findByText('No items in your library yet.')).toBeInTheDocument()
  })

  it('opens the PAGE holding a #media-<id> target that is not on the loaded page, not the whole section', async () => {
    const entries = Array.from({ length: 40 }, (_, i) =>
      makeEntry({}, { name: `Film ${String(i).padStart(2, '0')}` }))
    installLibraryHandlers(entries)
    const target = entries[35]
    mockedGetEntryForMedia.mockResolvedValue(target)
    vi.mocked(libraryApi.getLibrarySectionIds).mockResolvedValue(
      [...entries].sort((a, b) => a.mediaItem.name.localeCompare(b.mediaItem.name)).map(e => e.mediaItem.id))

    renderWithProviders(<LibraryPage />, { initialEntries: [`/library#media-${target.mediaItem.id}`] })

    // Not on the first page of 24: its position in the section decides which page to open (the second), never the whole section.
    expect(await screen.findByText('Film 35')).toBeInTheDocument()
    expect(mockedGetEntryForMedia).toHaveBeenCalledWith(target.mediaItem.id)
    expect(mockedGetSectionPage).toHaveBeenLastCalledWith(expect.objectContaining({ page: 2, perPage: 24 }))
    expect(mockedGetSectionPage).not.toHaveBeenCalledWith(expect.objectContaining({ perPage: 0 }))
  })

  it('scrolls to a #media-<id> target once, and does not pull the page back when the library refreshes', async () => {
    // Live (2026-10-22): browsing anime movies was choppy and kept dumping the user back around Harry Potter, the item whose
    // #media-<id> was still in the address; every library refresh while a film played re-ran the scroll.
    const entries = Array.from({ length: 5 }, (_, i) => makeEntry({}, { name: `Film ${i}` }))
    installLibraryHandlers(entries)
    const scrollIntoView = vi.fn()
    Element.prototype.scrollIntoView = scrollIntoView
    const { queryClient } = await import('@/test/test-utils').then(m => ({ queryClient: m.createTestQueryClient() }))

    renderWithProviders(<LibraryPage />, { initialEntries: [`/library#media-${entries[2].mediaItem.id}`], queryClient })
    await waitFor(() => expect(scrollIntoView).toHaveBeenCalledTimes(1), { timeout: 3000 })

    await queryClient.invalidateQueries({ queryKey: ['library'] })
    await new Promise(r => setTimeout(r, 900))

    expect(scrollIntoView).toHaveBeenCalledTimes(1)
  })

  it('closing one section does not re-render the cards of the others', async () => {
    // Live (2026-10-22): closing the Anime fold took seconds on a phone because every card on the page re-rendered.
    const movies = Array.from({ length: 5 }, (_, i) => makeEntry({}, { name: `Movie ${i}`, mediaTypeName: 'Movies' }))
    const anime = Array.from({ length: 3 }, (_, i) => makeEntry({}, { name: `Anime ${i}`, mediaTypeName: 'Anime' }))
    installLibraryHandlers([...movies, ...anime])
    for (const k of Object.keys(posterRenders)) delete posterRenders[k]
    renderWithProviders(<LibraryPage />)
    await screen.findByText('Anime 0')
    await screen.findByText('Movie 0')
    const before = posterRenders['Movie 0']
    const user = userEvent.setup()

    await user.click(screen.getByRole('button', { name: /Anime/ }))

    expect(screen.queryByText('Anime 0')).not.toBeInTheDocument()
    expect(posterRenders['Movie 0']).toBe(before)
  })

  it('draws a long section in batches as its end nears the screen, not all at once', async () => {
    // Live (2026-10-22): "Show all" on thousands of films put ~100,000 elements on the page; every open or close took seconds.
    const entries = Array.from({ length: 300 }, (_, i) => makeEntry({}, { name: `Film ${String(i).padStart(3, '0')}` }))
    installLibraryHandlers(entries)
    localStorage.setItem(PREFS_KEY, JSON.stringify({ sortBy: 'name', sortDir: 'asc', pageSizePreset: 'all', groupMoviesIntoCollections: true }))
    let fire: (() => void) | null = null
    class FakeObserver {
      constructor(cb: (e: { isIntersecting: boolean }[]) => void) { fire = () => cb([{ isIntersecting: true }]) }
      observe() {}
      disconnect() {}
    }
    vi.stubGlobal('IntersectionObserver', FakeObserver)
    renderWithProviders(<LibraryPage />)

    await screen.findByText('Film 000')
    expect(document.querySelectorAll('[id^="media-"]').length).toBe(96)

    // Everything fetched so far (96 rows) is on screen, so reaching the end asks the server for more -- twice as many, not all 300.
    await waitFor(() => expect(fire).not.toBeNull())
    fire!()
    await waitFor(() => expect(mockedGetSectionPage).toHaveBeenCalledWith(expect.objectContaining({ page: 1, perPage: 192 })))
    expect(mockedGetSectionPage).not.toHaveBeenCalledWith(expect.objectContaining({ perPage: 0 }))
    expect(mockedGetSectionPage).not.toHaveBeenCalledWith(expect.objectContaining({ perPage: 300 }))
    // ...and a fresh look at the end (the observer is re-armed when rows arrive) draws the next batch of cards.
    await waitFor(() => { fire!(); expect(document.querySelectorAll('[id^="media-"]').length).toBe(192) })
    vi.unstubAllGlobals()
  })

  it('keeps the cards on screen while a section fetches more rows (the list never empties, so the scroll position survives)', async () => {
    // Live (2026-10-22): reaching the end of a section emptied it for ~0.8 s until the larger fetch returned; the page shrank,
    // the scroll position was clamped to the top, and the person was dropped near the start of the list.
    const entries = Array.from({ length: 300 }, (_, i) => makeEntry({}, { name: `Film ${String(i).padStart(3, '0')}` }))
    installLibraryHandlers(entries)
    const serve = mockedGetSectionPage.getMockImplementation()!
    mockedGetSectionPage.mockImplementation(async p => { if (p.perPage > 96) await new Promise(r => setTimeout(r, 300)); return serve(p) })
    localStorage.setItem(PREFS_KEY, JSON.stringify({ sortBy: 'name', sortDir: 'asc', pageSizePreset: 'all', groupMoviesIntoCollections: true }))
    let fire: (() => void) | null = null
    class FakeObserver {
      constructor(cb: (e: { isIntersecting: boolean }[]) => void) { fire = () => cb([{ isIntersecting: true }]) }
      observe() {}
      disconnect() {}
    }
    vi.stubGlobal('IntersectionObserver', FakeObserver)
    renderWithProviders(<LibraryPage />)
    await screen.findByText('Film 000')
    await waitFor(() => expect(fire).not.toBeNull())

    const counts: number[] = []
    const watcher = new MutationObserver(() => counts.push(document.querySelectorAll('[id^="media-"]').length))
    watcher.observe(document.body, { childList: true, subtree: true })
    fire!()
    await waitFor(() => expect(mockedGetSectionPage).toHaveBeenCalledWith(expect.objectContaining({ perPage: 192 })))
    await new Promise(r => setTimeout(r, 500))                     // the larger fetch returns
    watcher.disconnect()
    vi.unstubAllGlobals()

    expect(Math.min(...counts, 96)).toBeGreaterThanOrEqual(96)
  })
})
