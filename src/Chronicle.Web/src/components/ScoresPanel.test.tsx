import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { describe, expect, it, vi } from 'vitest'
import { ScoresPanel } from './ScoresPanel'
import * as pluginsApi from '@/api/plugins'
import type { ItemScore } from '@/types'

vi.mock('@/api/plugins')

const score = (o: Partial<ItemScore>): ItemScore => ({
  key: 'imdb', displayName: 'IMDb', value: 8.2, scale: 10, display: 'outOf', normalized: 82, source: 'IMDb',
  pluginId: 'chronicle.plugin.imdb', alsoFrom: [], ...o,
})

function renderPanel(scores?: ItemScore[] | null) {
  vi.mocked(pluginsApi.listPlugins).mockResolvedValue([])
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <ScoresPanel scores={scores} />
    </QueryClientProvider>,
  )
}

describe('ScoresPanel', () => {
  it('renders nothing when there are no scores', () => {
    renderPanel(null)
    expect(screen.queryByTestId('scores-panel')).toBeNull()
    renderPanel([])
    expect(screen.queryByTestId('scores-panel')).toBeNull()
  })

  it('shows each score in its own style, with a star only where declared', () => {
    const { container } = renderPanel([
      score({ star: 'single', votes: 787753 }),
      score({ key: 'tmdb', displayName: 'TMDB', display: 'percent', value: 79.84, scale: 100, normalized: 79.84, source: 'TMDB' }),
    ])
    expect(screen.getByLabelText('IMDb score 8.2 out of 10, 787,753 votes')).toBeTruthy()
    expect(screen.getByLabelText('TMDB score 80 percent')).toBeTruthy()
    expect(screen.getByText('788K')).toBeTruthy()
    expect(container.querySelectorAll('svg').length).toBe(2) // one star = base + fill glyph; TMDB has none
  })

  it('links only when the source gave an address, and shows an attribution line', () => {
    renderPanel([score({ url: 'https://www.imdb.com/title/tt1/', attribution: 'Ratings from IMDb.' }), score({ key: 'x', displayName: 'X' })])
    expect(screen.getAllByRole('link')).toHaveLength(1)
    // the attribution sits inside the badge that owns it, not beside or under the row
    expect(screen.getByText('Ratings from IMDb.').closest('a')).toBe(screen.getAllByRole('link')[0])
  })

  it('draws a row of five stars for a source that declares one', () => {
    const { container } = renderPanel([score({ key: 'h', displayName: 'Hardcover', value: 4.2, scale: 5, normalized: 84, star: 'row' })])
    expect(container.querySelectorAll('svg').length).toBe(10)
  })
})
