import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen } from '@testing-library/react'
import CollectionMetadataBox from './CollectionMetadataBox'
import { renderWithProviders } from '@/test/test-utils'
import type { CollectionInfo, CollectionMember } from '@/api/collections'
import * as collectionsApi from '@/api/collections'

vi.mock('@/api/collections')

const mockedGetCollection = vi.mocked(collectionsApi.getCollection)

function makeMember(overrides: Partial<CollectionMember> = {}): CollectionMember {
  return {
    id: 1,
    name: 'Fast X',
    year: 2023,
    posterUrl: 'https://img.example/fast-x.jpg',
    inLibrary: true,
    libraryStatus: null,
    resumePositionPercent: null,
    rating: 7.5,
    userRating: null,
    userRatingSource: null,
    isStub: false,
    hasFile: true,
    ...overrides,
  }
}

function makeCollection(movies: CollectionMember[]): CollectionInfo {
  return {
    id: 100,
    name: 'The Fast and the Furious Collection',
    posterUrl: null,
    overview: null,
    movies,
    supportsRebuild: true,
  }
}

beforeEach(() => {
  vi.clearAllMocks()
})

describe('CollectionMetadataBox', () => {
  it('shows a full progress bar for a Completed movie, not none', async () => {
    // Root-caused live (2026-09-09): a Completed movie's resumePositionPercent is cleared
    // server-side, so its poster showed no progress bar at all inside a collection view --
    // indistinguishable from never having been started.
    mockedGetCollection.mockResolvedValue(makeCollection([
      makeMember({ id: 1, name: 'Fast X', libraryStatus: 'Completed', resumePositionPercent: null }),
    ]))

    renderWithProviders(<CollectionMetadataBox mediaItemId={1} />)

    const track = await screen.findByTitle('100% watched')
    const fill = track.querySelector('div')
    expect(fill).toHaveStyle({ width: '100%' })
  })

  it('shows the raw resume position for a Watching movie', async () => {
    mockedGetCollection.mockResolvedValue(makeCollection([
      makeMember({ id: 2, name: 'Fast Five', libraryStatus: 'Watching', resumePositionPercent: 43.79 }),
    ]))

    renderWithProviders(<CollectionMetadataBox mediaItemId={1} />)

    const track = await screen.findByTitle('44% watched')
    const fill = track.querySelector('div')
    expect(fill).toHaveStyle({ width: '43.79%' })
  })

  it('shows a full progress bar for a Completed movie even when its poster is fanart.tv-hosted', async () => {
    // The FanartImage branch (isFanartUrl(movie.posterUrl)) is a SEPARATE component from
    // PosterImage -- easy to fix one and miss the other, which is exactly what happened
    // initially here, since most real posters in this app ARE fanart.tv-hosted.
    mockedGetCollection.mockResolvedValue(makeCollection([
      makeMember({
        id: 1, name: 'Fast X', libraryStatus: 'Completed', resumePositionPercent: null,
        posterUrl: 'https://assets.fanart.tv/fanart/fast-x-645c5332aca7f.jpg',
      }),
    ]))

    renderWithProviders(<CollectionMetadataBox mediaItemId={1} />)

    const track = await screen.findByTitle('100% watched')
    const fill = track.querySelector('div')
    expect(fill).toHaveStyle({ width: '100%' })
  })

  it('shows no progress bar for an untracked movie', async () => {
    mockedGetCollection.mockResolvedValue(makeCollection([
      makeMember({ id: 3, name: 'Fast Forever', inLibrary: false, libraryStatus: null, resumePositionPercent: null }),
    ]))

    renderWithProviders(<CollectionMetadataBox mediaItemId={1} />)

    await screen.findByText('Fast Forever')
    expect(screen.queryByTitle(/% watched/)).not.toBeInTheDocument()
  })
})
