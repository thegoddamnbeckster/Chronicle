import { screen, within } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import MetadataAssignmentPage from './MetadataAssignmentPage'
import * as settingsApi from '@/api/settings'
import { renderWithProviders } from '@/test/test-utils'
import type { User } from '@/types'

vi.mock('@/api/settings')
vi.mock('@/api/media', () => ({
  getMediaTypes: vi.fn().mockResolvedValue([]),
  resetOverridesForMediaType: vi.fn(),
  resetAllOverrides: vi.fn(),
  getOverrideResetProgress: vi.fn(),
}))

const ADMIN: User = { id: 1, username: 'admin', email: null, displayName: null, isAdmin: true, showDiagnostics: false, showNowPlayingBanner: false, showAllCredits: false }

const TMDB = { pluginId: 'chronicle.plugin.tmdb', name: 'TMDB', iconUrl: null }
const IGDB = { pluginId: 'chronicle.plugin.igdb', name: 'IGDB', iconUrl: null }
const IMDB = { pluginId: 'chronicle.plugin.imdb', name: 'IMDb', iconUrl: null }

/** What the server sends: a movie collection and a video game series are different cards from different sources. */
function config(): settingsApi.MetadataAssignmentConfig {
  return {
    assignments: {
      movies: { title: [TMDB.pluginId, IMDB.pluginId] },
      'movies.collection': { title: [TMDB.pluginId] },
      game: { title: [IGDB.pluginId, IMDB.pluginId] },
      'game.collection': { title: [IGDB.pluginId] },
    },
    assignableFields: {
      movies: ['cast', 'title'],
      'movies.collection': ['logo_url', 'overview', 'poster_url', 'title'],
      game: ['cast', 'title'],
      'game.collection': ['overview', 'title'],
    },
    availablePlugins: {
      movies: [TMDB, IMDB],
      'movies.collection': [TMDB],
      game: [IGDB, IMDB],
      'game.collection': [IGDB],
    },
    mediaTypeDisplayNames: {
      movies: 'Movies',
      'movies.collection': 'Movies Collections',
      game: 'Video Games',
      'game.collection': 'Video Games Collections',
    },
    displayOrder: {},
    fieldPlugins: {
      movies: { cast: [TMDB.pluginId, IMDB.pluginId], title: [TMDB.pluginId, IMDB.pluginId] },
      'movies.collection': { logo_url: [TMDB.pluginId], overview: [TMDB.pluginId], poster_url: [TMDB.pluginId], title: [TMDB.pluginId] },
      game: { cast: [IGDB.pluginId, IMDB.pluginId], title: [IGDB.pluginId, IMDB.pluginId] },
      'game.collection': { overview: [IGDB.pluginId], title: [IGDB.pluginId] },
    },
  } as settingsApi.MetadataAssignmentConfig
}

describe('MetadataAssignmentPage collection cards', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.mocked(settingsApi.getMetadataAssignment).mockResolvedValue(config())
  })

  it('shows a card for movie collections and one for video game collections, each under its own type', async () => {
    renderWithProviders(<MetadataAssignmentPage />, { user: ADMIN })

    const headings = (await screen.findAllByRole('heading', { level: 2 })).map(h => h.textContent)
    expect(headings).toEqual(expect.arrayContaining(['Movies', 'Movies Collections', 'Video Games', 'Video Games Collections']))
    expect(headings.indexOf('Movies Collections')).toBe(headings.indexOf('Movies') + 1)
    expect(headings.indexOf('Video Games Collections')).toBe(headings.indexOf('Video Games') + 1)
  })

  it('offers a collection card only the sources that describe collections, not every source of the type', async () => {
    renderWithProviders(<MetadataAssignmentPage />, { user: ADMIN })

    const heading = await screen.findByRole('heading', { level: 2, name: 'Video Games Collections' })
    const card = heading.closest('section') as HTMLElement
    expect(within(card).getAllByText('IGDB').length).toBeGreaterThan(0)
    expect(within(card).queryByText('IMDb')).toBeNull()   // IMDb has no collections: it is on the Video Games card, not this one
  })

  it('names a collection field a collection can have, with a label rather than the raw field name', async () => {
    renderWithProviders(<MetadataAssignmentPage />, { user: ADMIN })

    const heading = await screen.findByRole('heading', { level: 2, name: 'Movies Collections' })
    const card = heading.closest('section') as HTMLElement
    expect(within(card).getByText('Logo Image')).toBeInTheDocument()
    expect(within(card).queryByText('logo_url')).toBeNull()
  })
})
