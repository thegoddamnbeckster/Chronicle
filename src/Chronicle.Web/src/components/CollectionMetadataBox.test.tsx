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
    lastKnownProgressPercent: null,
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
  it('shows the actual last-scrobbled percent for a Completed movie, not a flat 100%', async () => {
    // Per-user correction (2026-09-09): "I used 100 percent as an example. you need to use
    // the actual progress. so if the person stops watching something at 96 percent... you
    // show 96 percent". resumePositionPercent is cleared on completion (nothing to "resume"),
    // but lastKnownProgressPercent survives specifically so this case can show the real value.
    mockedGetCollection.mockResolvedValue(makeCollection([
      makeMember({
        id: 1, name: 'Fast X', libraryStatus: 'Completed',
        resumePositionPercent: null, lastKnownProgressPercent: 96,
      }),
    ]))

    renderWithProviders(<CollectionMetadataBox mediaItemId={1} />)

    const track = await screen.findByTitle('96% watched')
    const fill = track.querySelector('div')
    expect(fill).toHaveStyle({ width: '96%' })
  })

  it('falls back to a full progress bar for a Completed movie with no known percent', async () => {
    // e.g. marked Completed by hand, or imported from a watch-history sync that reports no
    // percentage at all -- there's no better information than "fully watched" to show.
    mockedGetCollection.mockResolvedValue(makeCollection([
      makeMember({ id: 1, name: 'Fast X', libraryStatus: 'Completed', resumePositionPercent: null, lastKnownProgressPercent: null }),
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
