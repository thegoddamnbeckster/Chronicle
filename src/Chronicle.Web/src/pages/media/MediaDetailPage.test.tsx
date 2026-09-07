import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Routes, Route } from 'react-router-dom'
import MediaDetailPage from './MediaDetailPage'
import { renderWithProviders } from '@/test/test-utils'
import type { MediaItem, LibraryEntry, User, PersonListItem } from '@/types'
import * as mediaApi from '@/api/media'
import * as libraryApi from '@/api/library'
import * as pluginsApi from '@/api/plugins'
import * as settingsApi from '@/api/settings'
import * as usersApi from '@/api/users'
import * as collectionsApi from '@/api/collections'
import * as duplicatesApi from '@/api/duplicates'
import { useAuth } from '@/hooks/useAuth'

vi.mock('@/api/media')
vi.mock('@/api/library')
vi.mock('@/api/plugins')
vi.mock('@/api/settings')
vi.mock('@/api/users')
vi.mock('@/api/collections')
vi.mock('@/api/duplicates')
vi.mock('@/hooks/useAuth')

const mockedGetMedia = vi.mocked(mediaApi.getMedia)
const mockedGetMediaChildren = vi.mocked(mediaApi.getMediaChildren)
const mockedGetMediaPeople = vi.mocked(mediaApi.getMediaPeople)
const mockedGetMediaTypes = vi.mocked(mediaApi.getMediaTypes)
const mockedGetNfoDetail = vi.mocked(mediaApi.getNfoDetail)
const mockedGetCollections = vi.mocked(mediaApi.getCollections)
const mockedSearchMedia = vi.mocked(mediaApi.searchMedia)
const mockedRefreshMedia = vi.mocked(mediaApi.refreshMedia)
const mockedReparentToCollection = vi.mocked(mediaApi.reparentToCollection)
const mockedUnparentFromCollection = vi.mocked(mediaApi.unparentFromCollection)
const mockedSetMediaOverride = vi.mocked(mediaApi.setMediaOverride)

const mockedGetLibrary = vi.mocked(libraryApi.getLibrary)
const mockedAddToLibrary = vi.mocked(libraryApi.addToLibrary)
const mockedUpdateLibraryEntry = vi.mocked(libraryApi.updateLibraryEntry)

const mockedListPlugins = vi.mocked(pluginsApi.listPlugins)
const mockedGetPluginDisplayOrder = vi.mocked(settingsApi.getPluginDisplayOrder)
const mockedGetMyPreferences = vi.mocked(usersApi.getMyPreferences)
const mockedUpdateMyPreferences = vi.mocked(usersApi.updateMyPreferences)
const mockedGetCollection = vi.mocked(collectionsApi.getCollection)
const mockedUseAuth = vi.mocked(useAuth)

const MEDIA_ID = 42

const ADMIN_USER: User = {
  id: 1,
  username: 'admin',
  email: 'admin@example.com',
  displayName: 'Admin',
  isAdmin: true,
  showDiagnostics: false,
  showNowPlayingBanner: true,
}

function makeItem(overrides: Partial<MediaItem> = {}): MediaItem {
  return {
    id: MEDIA_ID,
    mediaTypeId: 1,
    mediaTypeName: 'Movies',
    mediaTypeInternalName: 'movies',
    parentId: null,
    name: 'Test Movie',
    year: 2020,
    overview: 'An overview of the test movie.',
    posterUrl: 'https://img.example/poster.jpg',
    runtimeMinutes: 120,
    hierarchyLevel: 0,
    ancestors: [],
    number: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    externalIds: [],
    fileScannerMeta: null,
    pluginMetadata: {},
    refreshLogs: null,
    enrichmentStatuses: {},
    hasPhysicalFile: false,
    aliases: null,
    mergeHistory: [],
    resolvedMetadata: null,
    overrides: null,
    ...overrides,
  }
}

function makeLibraryEntry(item: MediaItem, overrides: Partial<LibraryEntry> = {}): LibraryEntry {
  return {
    id: 900,
    userId: 1,
    mediaItem: item,
    status: 'Watching',
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

function makePerson(overrides: Partial<PersonListItem> & { roles: string[] }): PersonListItem {
  return {
    id: Math.floor(Math.random() * 100000),
    name: 'Person',
    posterUrl: null,
    birthDate: null,
    deathDate: null,
    ...overrides,
  }
}

/** Renders the page at /media/:id through an actual Route so useParams() resolves mediaId,
 * exactly like the real router does — MediaDetailPage reads `id` via useParams<{id:string}>(). */
function renderMediaDetailPage(mediaId = MEDIA_ID) {
  return renderWithProviders(
    <Routes>
      <Route path="/media/:id" element={<MediaDetailPage />} />
    </Routes>,
    { initialEntries: [`/media/${mediaId}`] },
  )
}

beforeEach(() => {
  vi.clearAllMocks()

  mockedGetMedia.mockResolvedValue(makeItem())
  mockedGetMediaChildren.mockResolvedValue([])
  mockedGetMediaPeople.mockResolvedValue([])
  mockedGetMediaTypes.mockResolvedValue([])
  mockedGetNfoDetail.mockResolvedValue(null)
  mockedGetCollections.mockResolvedValue([])
  mockedSearchMedia.mockResolvedValue([])
  mockedRefreshMedia.mockResolvedValue(makeItem())
  mockedReparentToCollection.mockResolvedValue(makeItem())
  mockedUnparentFromCollection.mockResolvedValue(makeItem())
  mockedSetMediaOverride.mockResolvedValue(makeItem())

  mockedGetLibrary.mockResolvedValue([])
  mockedAddToLibrary.mockResolvedValue(makeLibraryEntry(makeItem()))
  mockedUpdateLibraryEntry.mockResolvedValue(makeLibraryEntry(makeItem()))

  mockedListPlugins.mockResolvedValue([])
  mockedGetPluginDisplayOrder.mockResolvedValue({})
  mockedGetMyPreferences.mockResolvedValue({})
  mockedUpdateMyPreferences.mockResolvedValue(undefined)
  // CollectionMetadataBox mounts whenever the item's media type is a flat collection type
  // (see the join/add-to-collection tests below) — reject so its own query settles to an
  // error state instead of hitting the real network from inside jsdom.
  mockedGetCollection.mockRejectedValue(new Error('not mocked in this test'))
  vi.mocked(duplicatesApi.unmergeItem).mockResolvedValue(undefined)

  mockedUseAuth.mockReturnValue({
    user: ADMIN_USER,
    loading: false,
    logout: vi.fn(),
    setUser: vi.fn(),
  })
})

describe('MediaDetailPage', () => {
  it('renders the loaded item\'s core info', async () => {
    renderMediaDetailPage()

    expect(await screen.findByRole('heading', { name: 'Test Movie' })).toBeInTheDocument()
    expect(screen.getByText('2020')).toBeInTheDocument()
    expect(screen.getByText('Movies')).toBeInTheDocument()
    expect(screen.getByText('120 min')).toBeInTheDocument()
    expect(screen.getByText('An overview of the test movie.')).toBeInTheDocument()
    expect(screen.getByAltText('Test Movie')).toHaveAttribute('src', 'https://img.example/poster.jpg')
    expect(mockedGetMedia).toHaveBeenCalledWith(MEDIA_ID)
  })

  it('adds to library with the status matching the button clicked', async () => {
    const user = userEvent.setup()
    renderMediaDetailPage()
    await screen.findByRole('heading', { name: 'Test Movie' })

    await user.click(screen.getByRole('button', { name: '+ Add to Library' }))
    expect(mockedAddToLibrary).toHaveBeenCalledWith(MEDIA_ID, 'Watching')

    await user.click(screen.getByRole('button', { name: 'Plan to Watch' }))
    expect(mockedAddToLibrary).toHaveBeenCalledWith(MEDIA_ID, 'PlanToWatch')
  })

  it('updates the library entry status through updateLibraryEntry', async () => {
    const user = userEvent.setup()
    const entry = makeLibraryEntry(makeItem(), { id: 777, status: 'Watching' })
    mockedGetLibrary.mockResolvedValue([entry])

    renderMediaDetailPage()
    await screen.findByRole('heading', { name: 'Test Movie' })

    const [statusSelect] = await screen.findAllByRole('combobox')
    await user.selectOptions(statusSelect, 'Completed')

    expect(mockedUpdateLibraryEntry).toHaveBeenCalledWith(777, { status: 'Completed', rating: undefined })
  })

  it('refreshes metadata and reflects the updated item returned by the API', async () => {
    const user = userEvent.setup()
    mockedRefreshMedia.mockResolvedValue(makeItem({ name: 'Refreshed Title' }))

    renderMediaDetailPage()
    await screen.findByRole('heading', { name: 'Test Movie' })

    await user.click(screen.getByRole('button', { name: '↻ Refresh All' }))

    expect(mockedRefreshMedia).toHaveBeenCalledWith(MEDIA_ID)
    expect(await screen.findByRole('heading', { name: 'Refreshed Title' })).toBeInTheDocument()
  })

  // --- Regression coverage for the collection-direction bug confirmed via
  // chronicle-20260802.log: the two "join a collection" controls must pass mediaId and the
  // picked collection id to reparentToCollection in opposite argument positions depending on
  // which side of the relationship this page's item plays. Swapping them silently reparents
  // the wrong item (see the long comment above joinCollectionOpen in MediaDetailPage.tsx). ---

  it('joining an existing collection reparents THIS item under the picked collection (mediaId, collectionId)', async () => {
    const user = userEvent.setup()
    // Flat collection type, standalone item (no children, no collection: external id) ->
    // renders "Add to a Collection" (the inverse control), gated on !isKnownCollection.
    mockedGetMediaTypes.mockResolvedValue([
      { id: 1, name: 'movies', displayName: 'Movies', hierarchyLevels: 1 },
    ])
    mockedGetCollections.mockResolvedValue([
      { id: 501, name: 'Die Hard Collection', posterUrl: null, itemCount: 5, mediaTypeId: 1 },
    ])

    renderMediaDetailPage()
    await screen.findByRole('heading', { name: 'Test Movie' })

    await user.click(screen.getByRole('button', { name: 'Add to a Collection' }))
    await user.type(screen.getByPlaceholderText('Search collections to add this into…'), 'Die Hard')

    const result = await screen.findByRole('button', { name: /Die Hard Collection/ })
    await user.click(result)

    expect(mockedReparentToCollection).toHaveBeenCalledWith(MEDIA_ID, 501)
  })

  it('adding a movie into this collection reparents the PICKED movie under this item (movieId, mediaId)', async () => {
    const user = userEvent.setup()
    // Flat collection type, already a known collection (has a child) -> renders the
    // "Add to Collection" control (this item as the collection root).
    mockedGetMediaTypes.mockResolvedValue([
      { id: 1, name: 'movies', displayName: 'Movies', hierarchyLevels: 1 },
    ])
    mockedGetMediaChildren.mockResolvedValue([
      makeItem({ id: 100, name: 'Existing Member', parentId: MEDIA_ID, hierarchyLevel: 1 }),
    ])
    mockedSearchMedia.mockResolvedValue([
      makeItem({ id: 200, name: 'Standalone Movie', parentId: null, mediaTypeId: 1 }),
    ])

    renderMediaDetailPage()
    await screen.findByRole('heading', { name: 'Test Movie' })

    await user.click(screen.getByRole('button', { name: 'Add to Collection' }))
    await user.type(screen.getByPlaceholderText('Search standalone movies to add…'), 'Standalone')

    const result = await screen.findByRole('button', { name: /Standalone Movie/ })
    await user.click(result)

    expect(mockedReparentToCollection).toHaveBeenCalledWith(200, MEDIA_ID)
  })

  it('splits on-screen talent (actor) from crew (director) per the 2026-08-30 grouping rule', async () => {
    const actor = makePerson({ id: 10, name: 'Onscreen Actor', roles: ['Actor'] })
    const director = makePerson({ id: 11, name: 'Behind Camera Director', roles: ['Director'] })
    mockedGetMediaPeople.mockResolvedValue([actor, director])

    renderMediaDetailPage()
    await screen.findByRole('heading', { name: 'Test Movie' })

    // The actor renders in the top on-screen row.
    expect(await screen.findByText('Onscreen Actor')).toBeInTheDocument()

    // The director renders inside the "Crew" fold, not the top row.
    const crewHeader = screen.getByText('Crew')
    const crewFold = crewHeader.closest('div') as HTMLElement
    expect(within(crewFold).getByText('Behind Camera Director')).toBeInTheDocument()
    expect(within(crewFold).queryByText('Onscreen Actor')).not.toBeInTheDocument()
  })

  it('pins the poster image to a slot via the lightbox image controls', async () => {
    const user = userEvent.setup()
    renderMediaDetailPage()
    await screen.findByRole('heading', { name: 'Test Movie' })

    await user.click(screen.getByAltText('Test Movie'))

    const posterChip = await screen.findByRole('button', { name: 'Poster' })
    await user.click(posterChip)

    expect(mockedSetMediaOverride).toHaveBeenCalledWith(
      MEDIA_ID, 'poster_url', 'https://img.example/poster.jpg', undefined, undefined,
    )
  })
})
