import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Routes, Route } from 'react-router-dom'
import PersonDetailPage from './PersonDetailPage'
import { renderWithProviders } from '@/test/test-utils'
import type { MediaItem, User } from '@/types'
import * as mediaApi from '@/api/media'
import * as peopleApi from '@/api/people'
import { useAuth } from '@/hooks/useAuth'

vi.mock('@/api/media')
vi.mock('@/api/people')
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

function setUser(showAllCredits: boolean) {
  vi.mocked(useAuth).mockReturnValue({
    user: { ...USER, showAllCredits }, loading: false, logout: vi.fn(), setUser: vi.fn(),
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
    await userEvent.setup().click(await screen.findByLabelText(/show every credit/i))

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
    expect(screen.getByLabelText(/show every credit/i)).toBeChecked()
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
