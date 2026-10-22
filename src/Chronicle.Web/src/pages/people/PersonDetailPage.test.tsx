import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Routes, Route } from 'react-router-dom'
import PersonDetailPage from './PersonDetailPage'
import { renderWithProviders } from '@/test/test-utils'
import type { MediaItem, User } from '@/types'
import * as mediaApi from '@/api/media'
import * as peopleApi from '@/api/people'
import * as pluginsApi from '@/api/plugins'
import * as settingsApi from '@/api/settings'
import * as usersApi from '@/api/users'
import * as libraryApi from '@/api/library'
import { useAuth } from '@/hooks/useAuth'

vi.mock('@/api/media')
vi.mock('@/api/people')
vi.mock('@/api/plugins')
vi.mock('@/api/settings')
vi.mock('@/api/users')
vi.mock('@/api/library')
// The author record is the media detail page itself, embedded; it has its own suite.
vi.mock('@/pages/media/MediaDetailPage', () => ({ default: ({ embeddedId }: { embeddedId?: number }) => <div>embedded record {embeddedId}</div> }))
vi.mock('@/hooks/useAuth')

const PERSON_ID = 7

const USER: User = {
  id: 1, username: 'u', email: null, displayName: null, isAdmin: false,
  showDiagnostics: false, showNowPlayingBanner: true, showAllCredits: false,
}

function person(): MediaItem {
  return {
    id: PERSON_ID, mediaTypeId: 2, mediaTypeName: 'People', mediaTypeInternalName: 'people', parentId: null,
    name: 'Keanu Reeves', year: null, overview: null, posterUrl: null, runtimeMinutes: null, hierarchyLevel: 0,
    ancestors: [], number: null, createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
    externalIds: [], fileScannerMeta: null, pluginMetadata: {}, refreshLogs: null, enrichmentStatuses: {},
    hasPhysicalFile: false, aliases: null, mergeHistory: [], resolvedMetadata: null, overrides: null,
  } as MediaItem
}

function setUser(showAllCredits: boolean, isAdmin = false) {
  vi.mocked(useAuth).mockReturnValue({
    user: { ...USER, showAllCredits, isAdmin }, loading: false, logout: vi.fn(), setUser: vi.fn(),
  })
}

function renderPage() {
  return renderWithProviders(
    <Routes><Route path="/people/:id" element={<PersonDetailPage />} /></Routes>,
    { initialEntries: [`/people/${PERSON_ID}`] },
  )
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(mediaApi.getMedia).mockResolvedValue(person())
  vi.mocked(peopleApi.getPersonHeadshots).mockResolvedValue([])
  vi.mocked(peopleApi.getPersonCredits).mockResolvedValue([
    { role: 'Actor', items: [{ mediaItemId: 1, name: 'Library Movie', posterUrl: null, year: 1999, mediaTypeName: 'movies', characterName: null }] },
  ])
  vi.mocked(peopleApi.getPersonAllCredits).mockResolvedValue({
    incomplete: false,
    groups: [{
      role: 'Actor',
      items: [
        { mediaItemId: 1, name: 'Library Movie', posterUrl: null, year: 1999, mediaTypeName: 'movies', characterName: null },
        { mediaItemId: null, name: 'Outside Movie', posterUrl: null, year: 1990, mediaTypeName: 'movies', characterName: null },
      ],
    }],
  })
  vi.mocked(pluginsApi.listPlugins).mockResolvedValue([])
  vi.mocked(settingsApi.getPluginDisplayOrder).mockResolvedValue({})
  vi.mocked(usersApi.getMyPreferences).mockResolvedValue({})
  vi.mocked(libraryApi.getLibrarySectionIds).mockResolvedValue([])
  vi.mocked(mediaApi.getMediaTypes).mockResolvedValue([])
  setUser(false)
})

describe('PersonDetailPage "show every credit"', () => {
  it('is off by default: shows only library credits and never fetches the full list', async () => {
    renderPage()

    expect(await screen.findByText('Library Movie')).toBeInTheDocument()
    expect(screen.queryByText('Outside Movie')).not.toBeInTheDocument()
    expect(peopleApi.getPersonAllCredits).not.toHaveBeenCalled()
  })

  it('checking the box shows titles that are not in the library, marked and not linked', async () => {
    renderPage()
    await userEvent.setup().click(await screen.findByRole('button', { name: /show all credits/i }))

    const outside = await screen.findByText('Outside Movie')
    expect(outside).toBeInTheDocument()
    expect(screen.getByText('Not in library')).toBeInTheDocument()
    expect(outside.closest('a')).toBeNull()
    expect(screen.getByText('Library Movie').closest('a')).not.toBeNull()
  })

  it('follows the Preferences default: on means the full list is shown without touching the box', async () => {
    setUser(true)
    renderPage()

    expect(await screen.findByText('Outside Movie')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /showing all credits/i })).toHaveAttribute('aria-pressed', 'true')
    expect(peopleApi.getPersonCredits).not.toHaveBeenCalled()
  })

  it('warns when the provider list could not be fetched', async () => {
    vi.mocked(peopleApi.getPersonAllCredits).mockResolvedValue({
      incomplete: true,
      groups: [{ role: 'Actor', items: [{ mediaItemId: 1, name: 'Library Movie', posterUrl: null, year: 1999, mediaTypeName: 'movies', characterName: null }] }],
    })
    setUser(true)
    renderPage()

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(/only the credits already in your library/i))
  })
})

describe('PersonDetailPage photo gallery: hiding a wrong photo', () => {
  const photo = (id: number, over: Partial<import('@/types').PersonHeadshot> = {}): import('@/types').PersonHeadshot => ({
    id, url: `https://img.example/${id}.jpg`, thumbnailUrl: null, source: 'tmdb',
    firstSeenAt: '2026-09-16T00:00:00Z', isCurrent: false, isHidden: false, ...over,
  })

  beforeEach(() => {
    vi.mocked(peopleApi.hidePersonHeadshot).mockResolvedValue()
    vi.mocked(peopleApi.restorePersonHeadshots).mockResolvedValue()
    setUser(false, true)          // hiding and restoring photos is an admin action (the server enforces it too)
  })

  it('offers no Hide or Restore to a person who is not an administrator', async () => {
    setUser(false, false)
    vi.mocked(peopleApi.getPersonHeadshots).mockResolvedValue([photo(1, { isCurrent: true }), photo(2), photo(3, { isHidden: true })])
    renderPage()

    await screen.findByText('Photos')
    expect(screen.queryByRole('button', { name: /hide this photo/i })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /restore hidden/i })).not.toBeInTheDocument()
  })

  it('asks for hidden photos too, so it can offer to bring them back', async () => {
    vi.mocked(peopleApi.getPersonHeadshots).mockResolvedValue([photo(1, { isCurrent: true }), photo(2)])
    renderPage()

    await screen.findByRole('button', { name: /hide this photo/i })
    expect(peopleApi.getPersonHeadshots).toHaveBeenCalledWith(PERSON_ID, true)
  })

  it('offers Hide on every photo except the one currently shown', async () => {
    vi.mocked(peopleApi.getPersonHeadshots).mockResolvedValue([photo(1, { isCurrent: true }), photo(2), photo(3)])
    renderPage()

    const hide = await screen.findAllByRole('button', { name: /hide this photo/i })
    expect(hide).toHaveLength(2)
  })

  it('hides the chosen photo when its button is pressed', async () => {
    vi.mocked(peopleApi.getPersonHeadshots).mockResolvedValue([photo(1, { isCurrent: true }), photo(2)])
    renderPage()

    await userEvent.setup().click(await screen.findByRole('button', { name: /hide this photo/i }))

    await waitFor(() => expect(peopleApi.hidePersonHeadshot).toHaveBeenCalledWith(PERSON_ID, 2))
  })

  it('does not show a hidden photo, and offers to restore the hidden ones', async () => {
    vi.mocked(peopleApi.getPersonHeadshots).mockResolvedValue([
      photo(1, { isCurrent: true }), photo(2), photo(3, { isHidden: true }), photo(4, { isHidden: true }),
    ])
    renderPage()

    // current + one other visible photo: a single Hide button (for photo 2), not one per stored row
    expect(await screen.findAllByRole('button', { name: /hide this photo/i })).toHaveLength(1)
    const restore = screen.getByRole('button', { name: /restore hidden \(2\)/i })
    await userEvent.setup().click(restore)
    await waitFor(() => expect(peopleApi.restorePersonHeadshots).toHaveBeenCalledWith(PERSON_ID))
  })

  it('shows no restore button when nothing is hidden', async () => {
    vi.mocked(peopleApi.getPersonHeadshots).mockResolvedValue([photo(1, { isCurrent: true }), photo(2)])
    renderPage()

    await screen.findByRole('button', { name: /hide this photo/i })
    expect(screen.queryByRole('button', { name: /restore hidden/i })).not.toBeInTheDocument()
  })
})

describe('PersonDetailPage: a person who is also an author', () => {
  const AUTHOR_ID = 900

  it('shows the whole author record on the same page, with no separate "open on its own page" link', async () => {
    vi.mocked(mediaApi.getMedia).mockResolvedValue({ ...person(), linkedItems: [{ itemId: AUTHOR_ID, name: 'Keanu Reeves', mediaTypeName: 'Audiobooks', isPerson: false }] } as MediaItem)

    renderPage()

    expect(await screen.findByText(`embedded record ${AUTHOR_ID}`)).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: /open on its own page/i })).not.toBeInTheDocument()
  })

  it('shows nothing extra for a person with no author record', async () => {
    renderPage()

    await screen.findByText('Library Movie')
    expect(screen.queryByRole('link', { name: /open on its own page/i })).not.toBeInTheDocument()
  })
})

describe('PersonDetailPage: Refresh All and the plugin panels', () => {
  it('has a Refresh All that refreshes the person and every linked author record', async () => {
    const linked = { linkedItems: [{ itemId: 900, name: 'Keanu Reeves', mediaTypeName: 'Audiobooks', isPerson: false }] }
    vi.mocked(mediaApi.getMedia).mockImplementation(async id => (id === 900
      ? { ...person(), id: 900, mediaTypeName: 'Audiobooks', mediaTypeInternalName: 'audiobooks' }
      : { ...person(), ...linked }) as MediaItem)
    vi.mocked(mediaApi.getMediaChildren).mockResolvedValue([])
    vi.mocked(mediaApi.refreshMedia).mockResolvedValue({ ...person(), ...linked } as MediaItem)
    renderPage()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: /refresh all/i }))

    await waitFor(() => expect(mediaApi.refreshMedia).toHaveBeenCalledWith(PERSON_ID))
    await waitFor(() => expect(mediaApi.refreshMedia).toHaveBeenCalledWith(900))
  })

  it("shows the person's own plugin panels", async () => {
    vi.mocked(mediaApi.getMedia).mockResolvedValue({
      ...person(), pluginMetadata: { 'chronicle.plugin.tmdb': {} }, enrichmentStatuses: { 'chronicle.plugin.tmdb': 'NotFound' },
    } as unknown as MediaItem)
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([
      { pluginId: 'chronicle.plugin.tmdb', name: 'TMDB', isEnabled: true, supportedMediaTypes: ['people'] },
    ] as never)
    renderPage()

    expect(await screen.findByRole('button', { name: /tmdb/i })).toBeInTheDocument()
  })
})

describe('PersonDetailPage: the author library navigation', () => {
  const linked = { linkedItems: [{ itemId: 900, name: 'Keanu Reeves', mediaTypeName: 'Audiobooks', isPerson: false }] }
  function mockPair() {
    vi.mocked(mediaApi.getMedia).mockImplementation(async id => (id === 900
      ? { ...person(), id: 900, mediaTypeName: 'Audiobooks', mediaTypeInternalName: 'audiobooks', parentId: null }
      : { ...person(), ...linked }) as MediaItem)
    vi.mocked(mediaApi.getMediaChildren).mockResolvedValue([])
  }

  it('has an up button to the author library, besides the one to People', async () => {
    mockPair()
    renderPage()

    const up = await screen.findByRole('link', { name: /↑ Audiobooks/ })
    expect(up).toHaveAttribute('href', '/library#media-900')
    expect(screen.getByRole('link', { name: /↑ People/ })).toBeInTheDocument()
  })

  it('opened from the audiobook library, Prev/Next walk that library, not a bogus "0 / n" People list', async () => {
    mockPair()
    renderWithProviders(
      <Routes><Route path="/people/:id" element={<PersonDetailPage />} /></Routes>,
      { initialEntries: [{ pathname: `/people/${PERSON_ID}`, state: { listIds: [899, 900, 901], listLabel: 'Audiobooks' } }] },
    )

    expect(await screen.findByText('2 / 3')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Prev/ })).toHaveAttribute('href', '/media/899')
    expect(screen.getByRole('link', { name: /Next/ })).toHaveAttribute('href', '/media/901')
    expect(screen.queryByText(/0 \/ 3/)).not.toBeInTheDocument()
  })
})

describe('PersonDetailPage: People Prev/Next when not opened from the People list', () => {
  beforeEach(() => {
    vi.mocked(mediaApi.getMedia).mockResolvedValue(person())   // earlier suites leave a linked-author mock behind
    sessionStorage.clear()                                      // ...and a remembered list
  })

  it('asks where the person sits in the list and shows Prev/Next to the neighbours', async () => {
    vi.mocked(peopleApi.getPersonNeighbors).mockResolvedValue({ index: 2, total: 10, prevId: 11, nextId: 13 })
    renderPage()

    expect(await screen.findByText('3 / 10')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /Prev/ })).toHaveAttribute('href', '/people/11')
    expect(screen.getByRole('link', { name: /Next/ })).toHaveAttribute('href', '/people/13')
  })

  it('a remembered People list still wins, and the neighbours are not fetched', async () => {
    renderWithProviders(
      <Routes><Route path="/people/:id" element={<PersonDetailPage />} /></Routes>,
      { initialEntries: [{ pathname: `/people/${PERSON_ID}`, state: { listIds: [5, PERSON_ID, 9], listLabel: 'People' } }] },
    )

    expect(await screen.findByText('2 / 3')).toBeInTheDocument()
    expect(peopleApi.getPersonNeighbors).not.toHaveBeenCalled()
  })

  it('shows no People Prev/Next when the person is alone in the list', async () => {
    vi.mocked(peopleApi.getPersonNeighbors).mockResolvedValue({ index: 0, total: 1, prevId: null, nextId: null })
    renderPage()

    await screen.findByText('Library Movie')
    expect(screen.queryByRole('link', { name: /Prev/ })).not.toBeInTheDocument()
  })
})

describe('PersonDetailPage: the backdrop', () => {
  beforeEach(() => { sessionStorage.clear() })

  it("shows the person's own backdrop like the media page does", async () => {
    vi.mocked(mediaApi.getMedia).mockResolvedValue({ ...person(), resolvedMetadata: { backdropUrl: 'https://img.example/b.jpg' } } as unknown as MediaItem)
    renderPage()

    expect(await screen.findByText('Backdrop')).toBeInTheDocument()
  })

  it('a person with none takes the backdrop of the linked record, the same entity', async () => {
    vi.mocked(mediaApi.getMedia).mockImplementation(async id => (id === 900
      ? { ...person(), id: 900, mediaTypeName: 'Audiobooks', mediaTypeInternalName: 'audiobooks', resolvedMetadata: { backdropUrl: 'https://img.example/a.jpg' } }
      : { ...person(), linkedItems: [{ itemId: 900, name: 'Keanu Reeves', mediaTypeName: 'Audiobooks', isPerson: false }] }) as unknown as MediaItem)
    renderPage()

    expect(await screen.findByText('Backdrop')).toBeInTheDocument()
  })

  it('shows no backdrop tag when neither has one', async () => {
    vi.mocked(mediaApi.getMedia).mockResolvedValue(person())
    renderPage()

    await screen.findByText('Library Movie')
    expect(screen.queryByText('Backdrop')).not.toBeInTheDocument()
  })
})

describe('PersonDetailPage: a card for every plugin that supports people', () => {
  it('shows a plugin card even when the person has no data or enrichment row for it', async () => {
    vi.mocked(mediaApi.getMedia).mockResolvedValue({ ...person(), pluginMetadata: {}, enrichmentStatuses: { 'chronicle.plugin.tmdb': 'Completed' } } as unknown as MediaItem)
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([
      { pluginId: 'chronicle.plugin.tmdb', name: 'TMDB', isEnabled: true, supportedMediaTypes: ['people'] },
      { pluginId: 'chronicle.plugin.imdb', name: 'IMDb', isEnabled: true, supportedMediaTypes: ['people', 'movies'] },
      { pluginId: 'chronicle.plugin.wikipedia', name: 'Wikipedia', isEnabled: true, supportedMediaTypes: ['people'] },
      { pluginId: 'chronicle.plugin.musicbrainz', name: 'MusicBrainz', isEnabled: true, supportedMediaTypes: ['music'] },
      { pluginId: 'chronicle.plugin.off', name: 'Off', isEnabled: false, supportedMediaTypes: ['people'] },
    ] as never)
    renderPage()

    expect(await screen.findByRole('button', { name: /tmdb/i })).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: /imdb/i })).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: /wikipedia/i })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /musicbrainz/i })).not.toBeInTheDocument()   // wrong type
    expect(screen.queryByRole('button', { name: /^off/i })).not.toBeInTheDocument()          // disabled
  })
})
