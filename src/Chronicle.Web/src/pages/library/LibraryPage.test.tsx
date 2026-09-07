import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import LibraryPage from './LibraryPage'
import { renderWithProviders } from '@/test/test-utils'
import type { LibraryEntry, LibraryStatus, MediaItem } from '@/types'
import * as libraryApi from '@/api/library'
import * as mediaApi from '@/api/media'

vi.mock('@/api/library')
vi.mock('@/api/media')

const mockedGetLibrary = vi.mocked(libraryApi.getLibrary)
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
    ...overrides,
  }
}

/** Serves getLibrary like the real endpoint: entries filtered by status if given.
 * LibraryPage always requests status=undefined (filtering happens client-side against the
 * full, unfiltered set) so this mainly just returns whatever fixture list is installed. */
function installGetLibraryHandler(entries: LibraryEntry[]) {
  mockedGetLibrary.mockImplementation(async (status?: LibraryStatus) => {
    if (!status) return entries
    return entries.filter(e => e.status === status)
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
    installGetLibraryHandler([
      makeEntry({ status: 'Watching' }, { name: 'Alpha Movie', mediaTypeName: 'Movies' }),
      makeEntry({ status: 'Completed' }, { name: 'Beta Show', mediaTypeName: 'TV Shows' }),
    ])

    renderWithProviders(<LibraryPage />)

    expect(await screen.findByText('Alpha Movie')).toBeInTheDocument()
    expect(screen.getByText('Beta Show')).toBeInTheDocument()
    expect(screen.getByText('Movies')).toBeInTheDocument()
    expect(screen.getByText('TV Shows')).toBeInTheDocument()

    // rootOnly=true and, since groupMoviesIntoCollections defaults to true,
    // includeMoviesInCollections must be false.
    expect(mockedGetLibrary).toHaveBeenCalledWith(undefined, 1, 0, true, false)
  })

  it('filters visible entries by status without changing the underlying fetch', async () => {
    const user = userEvent.setup()
    installGetLibraryHandler([
      makeEntry({ status: 'Watching' }, { name: 'Watching Movie' }),
      makeEntry({ status: 'Completed' }, { name: 'Completed Movie' }),
    ])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Watching Movie')
    expect(screen.getByText('Completed Movie')).toBeInTheDocument()

    const callsBefore = mockedGetLibrary.mock.calls.length
    await user.click(screen.getByRole('button', { name: 'Watching' }))

    await waitFor(() => {
      expect(screen.queryByText('Completed Movie')).not.toBeInTheDocument()
    })
    expect(screen.getByText('Watching Movie')).toBeInTheDocument()

    // The status filter is applied client-side against the already-fetched full list --
    // getLibrary is always called with status=undefined, so filtering must NOT trigger
    // another network round-trip.
    expect(mockedGetLibrary.mock.calls.length).toBe(callsBefore)
    expect(mockedGetLibrary).toHaveBeenLastCalledWith(undefined, 1, 0, true, false)
  })

  it('re-sorts the visible list when the sort order changes', async () => {
    const user = userEvent.setup()
    installGetLibraryHandler([
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
  })

  it('persists preference changes to localStorage', async () => {
    const user = userEvent.setup()
    installGetLibraryHandler([makeEntry({ status: 'Watching' }, { name: 'Some Movie' })])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Some Movie')

    await user.click(screen.getByRole('button', { name: 'Watching' }))

    await waitFor(() => {
      const stored = JSON.parse(localStorage.getItem(PREFS_KEY) ?? '{}')
      expect(stored.statusFilter).toBe('Watching')
    })
  })

  it('updates an entry status via the per-item select and calls the API with the right id/status', async () => {
    const user = userEvent.setup()
    const entry = makeEntry({ status: 'Unwatched' }, { name: 'Status Movie' })
    installGetLibraryHandler([entry])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Status Movie')

    const statusSelect = screen.getByDisplayValue('Unwatched')
    await user.selectOptions(statusSelect, 'Completed')

    await waitFor(() => {
      expect(mockedUpdateLibraryEntry).toHaveBeenCalledWith(entry.id, { status: 'Completed' })
    })
  })

  it('removes an entry only after the user confirms the browser dialog', async () => {
    const user = userEvent.setup()
    const entry = makeEntry({}, { name: 'Removable Movie' })
    installGetLibraryHandler([entry])

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

  it('deletes selected items in bulk through select mode and the confirmation modal', async () => {
    const user = userEvent.setup()
    const entry1 = makeEntry({}, { name: 'Bulk One' })
    const entry2 = makeEntry({}, { name: 'Bulk Two' })
    installGetLibraryHandler([entry1, entry2])

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
    installGetLibraryHandler([stubOnly, trulyMissing])

    renderWithProviders(<LibraryPage />)
    await screen.findByText('Stub Not Missing')
    await screen.findByText('Truly Missing')

    const stubCard = screen.getByText('Stub Not Missing').closest('div[id^="media-"]')!
    const missingCard = screen.getByText('Truly Missing').closest('div[id^="media-"]')!

    expect(within(stubCard as HTMLElement).queryByText('Missing')).not.toBeInTheDocument()
    expect(within(missingCard as HTMLElement).getByText('Missing')).toBeInTheDocument()
  })

  it('prefixes an episode/season entry with its show name via ancestors[0]', async () => {
    installGetLibraryHandler([
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
    installGetLibraryHandler([])
    renderWithProviders(<LibraryPage />)
    expect(await screen.findByText('No items in your library yet.')).toBeInTheDocument()
  })
})
