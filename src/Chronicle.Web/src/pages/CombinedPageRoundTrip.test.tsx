import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Routes, Route } from 'react-router-dom'
import MediaDetailPage from './media/MediaDetailPage'
import PersonDetailPage from './people/PersonDetailPage'
import { renderWithProviders } from '@/test/test-utils'
import type { MediaItem } from '@/types'
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
vi.mock('@/api/mediaRights')
vi.mock('@/api/collections')
vi.mock('@/api/duplicates')
vi.mock('@/hooks/useAuth')

const PERSON = 505711, AUTHOR = 386766, SERIES = 386771

function item(id: number, name: string, extra: Partial<MediaItem> = {}): MediaItem {
  return {
    id, mediaTypeId: id === PERSON ? 9 : 6, mediaTypeName: id === PERSON ? 'People' : 'Audiobooks',
    mediaTypeInternalName: id === PERSON ? 'people' : 'audiobooks', parentId: null, name, year: null, overview: null,
    posterUrl: null, runtimeMinutes: null, hierarchyLevel: id === SERIES ? 1 : 0, ancestors: [], number: null,
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', externalIds: [], fileScannerMeta: null,
    pluginMetadata: {}, refreshLogs: null, enrichmentStatuses: {}, hasPhysicalFile: false, aliases: null, mergeHistory: [],
    resolvedMetadata: null, overrides: null, ...extra,
  } as MediaItem
}

const ITEMS: Record<number, MediaItem> = {
  [PERSON]: item(PERSON, 'Shemer Kuznits', { linkedItems: [{ itemId: AUTHOR, name: 'Shemer Kuznits', mediaTypeName: 'Audiobooks', isPerson: false }] }),
  [AUTHOR]: item(AUTHOR, 'Shemer Kuznits', { linkedItems: [{ itemId: PERSON, name: 'Shemer Kuznits', mediaTypeName: 'People', isPerson: true }] }),
  [SERIES]: item(SERIES, 'New Era Online - Biomancer', { parentId: AUTHOR, ancestors: [{ id: AUTHOR, name: 'Shemer Kuznits' }] as never }),
}

beforeEach(() => {
  vi.clearAllMocks()
  sessionStorage.clear()
  vi.mocked(useAuth).mockReturnValue({ user: { id: 1, username: 'u', isAdmin: true, showAllCredits: false }, loading: false, logout: vi.fn(), setUser: vi.fn() } as never)
  vi.mocked(mediaApi.getMedia).mockImplementation(async id => ITEMS[id])
  vi.mocked(mediaApi.getMediaChildren).mockImplementation(async id => (id === AUTHOR ? [ITEMS[SERIES]] : []))
  vi.mocked(mediaApi.getMediaPeople).mockResolvedValue([])
  vi.mocked(mediaApi.getMediaTypes).mockResolvedValue([
    { id: 6, name: 'audiobooks', displayName: 'Audiobooks', hierarchyLevels: 3, hierarchyLabels: ['Author', 'Series', 'Book'] },
    { id: 9, name: 'people', displayName: 'People', hierarchyLevels: 1, isPersonType: true },
  ] as never)
  vi.mocked(mediaApi.getCollections).mockResolvedValue([])
  vi.mocked(peopleApi.getPersonHeadshots).mockResolvedValue([])
  vi.mocked(peopleApi.getPersonCredits).mockResolvedValue([])
  vi.mocked(peopleApi.getPersonNeighbors).mockResolvedValue(null)
  vi.mocked(pluginsApi.listPlugins).mockResolvedValue([])
  vi.mocked(settingsApi.getPluginDisplayOrder).mockResolvedValue({})
  vi.mocked(settingsApi.getAppSettings).mockResolvedValue({} as never)
  vi.mocked(usersApi.getMyPreferences).mockResolvedValue({})
  vi.mocked(usersApi.updateMyPreferences).mockResolvedValue({} as never)
  vi.mocked(libraryApi.getLibraryEntryForMedia).mockResolvedValue(null)
  vi.mocked(libraryApi.getLibraryEntriesForMediaIds).mockResolvedValue([])
  vi.mocked(libraryApi.getLibrarySectionIds).mockResolvedValue([])
})

describe('combined person + author page: going into a series and back out', () => {
  it('comes back showing the author record again, never the series', async () => {
    renderWithProviders(
      <Routes>
        <Route path="/people/:id" element={<PersonDetailPage />} />
        <Route path="/media/:id" element={<MediaDetailPage />} />
      </Routes>,
      { initialEntries: [`/people/${PERSON}`] },
    )
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: /Series \(1\)/ }))
    // the embedded author record lists the series; open it
    await user.click(await screen.findByRole('link', { name: /New Era Online - Biomancer/ }))
    // on the series page, go up to the author, which redirects to the combined page
    await user.click(await screen.findByRole('link', { name: /↑ Shemer Kuznits/ }))

    await waitFor(() => expect(screen.getAllByText('Shemer Kuznits').length).toBeGreaterThan(0))
    await screen.findByRole('link', { name: /New Era Online - Biomancer/ })      // the author's series list is back
    expect(screen.queryByRole('heading', { name: 'New Era Online - Biomancer' })).not.toBeInTheDocument()
  })

  it('and via the Back button', async () => {
    renderWithProviders(
      <Routes>
        <Route path="/people/:id" element={<PersonDetailPage />} />
        <Route path="/media/:id" element={<MediaDetailPage />} />
      </Routes>,
      { initialEntries: [`/people/${PERSON}`] },
    )
    const user = userEvent.setup()

    await user.click(await screen.findByRole('link', { name: /New Era Online - Biomancer/ }))
    await user.click(await screen.findByRole('button', { name: /← Back/ }))

    await screen.findByRole('link', { name: /New Era Online - Biomancer/ })
    expect(screen.queryByRole('heading', { name: 'New Era Online - Biomancer' })).not.toBeInTheDocument()
  })
})
