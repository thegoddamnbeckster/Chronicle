import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import GlobalSearch from './GlobalSearch'
import { renderWithProviders } from '@/test/test-utils'
import * as mediaApi from '@/api/media'
import type { MediaItem } from '@/types'

vi.mock('@/api/media')

function item(id: number, name: string): MediaItem {
  return { id, name, mediaTypeName: 'music', mediaTypeDisplayName: 'Music' } as unknown as MediaItem
}

beforeEach(() => vi.mocked(mediaApi.searchMedia).mockReset())

describe('GlobalSearch', () => {
  it('asks for 10 rows and shows every row the server returns, so no exact match is cut off', async () => {
    // The server grows its first page when there are more exact matches than 10.
    vi.mocked(mediaApi.searchMedia).mockResolvedValue(
      Array.from({ length: 11 }, (_, i) => item(i + 1, 'Invincible')))
    const user = userEvent.setup()
    renderWithProviders(<GlobalSearch />)

    await user.type(screen.getByLabelText('Search all media'), 'invincible')

    expect(await screen.findAllByRole('option')).toHaveLength(11)
    expect(mediaApi.searchMedia).toHaveBeenLastCalledWith('invincible', undefined, 1, true, 10)
  })
})
