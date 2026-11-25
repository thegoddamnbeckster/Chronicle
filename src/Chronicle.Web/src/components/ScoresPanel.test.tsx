import { fireEvent, render, screen, waitFor } from '@testing-library/react'
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

  it('falls back to a letter when the plugin icon fails to load', async () => {
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([{ pluginId: 'chronicle.plugin.imdb', iconUrl: '/api/v1/plugins/1/icon' } as never])
    const { container } = render(
      <QueryClientProvider client={new QueryClient()}><ScoresPanel scores={[score({})]} /></QueryClientProvider>,
    )
    await waitFor(() => expect(container.querySelector('img')).not.toBeNull())
    fireEvent.error(container.querySelector('img')!)
    expect(container.querySelector('img')).toBeNull()
    expect(screen.getByText('I')).toBeTruthy()
  })

  it('draws a row of five stars for a source that declares one', () => {
    const { container } = renderPanel([score({ key: 'h', displayName: 'Hardcover', value: 4.2, scale: 5, normalized: 84, star: 'row' })])
    expect(container.querySelectorAll('svg').length).toBe(10)
  })

  it('draws a ring with the whole-number percent, coloured by the tier the value falls in', () => {
    const { container } = renderPanel([score({
      key: 'tmdb', displayName: 'TMDB', display: 'percent', value: 79.84, scale: 100, normalized: 79.84, look: 'ring', label: 'User Score',
      tiers: [{ min: 0, color: '#DB2360' }, { min: 40, color: '#D2D531' }, { min: 70, color: '#21D07A', track: '#204529' }],
    })])
    const arcs = container.querySelectorAll('circle[stroke-linecap="round"]')
    expect(arcs).toHaveLength(1)
    expect(arcs[0].getAttribute('stroke')).toBe('#21D07A')
    expect(container.querySelector('[class*="ringNumber"]')?.textContent).toBe('80%')
    expect(screen.getByText('User Score')).toBeTruthy()
  })

  it('draws a coloured block (square or round) holding the number for the badge look', () => {
    const tiers = [{ min: 0, color: '#FF6874' }, { min: 40, color: '#FFBD3F' }, { min: 61, color: '#00CE7A' }]
    const { container } = renderPanel([
      score({ key: 'm', displayName: 'Metacritic', value: 73, scale: 100, normalized: 73, look: 'badge', label: 'Metascore', tiers }),
      score({ key: 'u', displayName: 'Metacritic', variant: 'users', value: 4, scale: 10, normalized: 40, look: 'badge', shape: 'circle', tiers }),
    ])
    const blocks = container.querySelectorAll('[class*="block"]')
    expect(blocks).toHaveLength(2)
    expect((blocks[0] as HTMLElement).style.background).toBe('rgb(0, 206, 122)')
    expect((blocks[1] as HTMLElement).className).toContain('circle')
    expect((blocks[1] as HTMLElement).style.background).toBe('rgb(255, 189, 63)')
  })

  it('draws a state icon beside the percent for the icon-percent look, and none when the tier names none', () => {
    const tiers = [{ min: 0, color: '#0AC855', icon: 'rotten' as const }, { min: 60, color: '#FA320A', icon: 'fresh' as const }]
    const { container } = renderPanel([
      score({ key: 'rt', displayName: 'Rotten Tomatoes', variant: 'critics', display: 'percent', value: 83, scale: 100, normalized: 83, look: 'icon-percent', label: 'Tomatometer', tiers }),
      score({ key: 'rt2', displayName: 'RT', display: 'percent', value: 30, scale: 100, normalized: 30, look: 'icon-percent', tiers }),
      score({ key: 'rt3', displayName: 'RT3', display: 'percent', value: 30, scale: 100, normalized: 30, look: 'icon-percent', tiers: [{ min: 0, color: '#111111' }] }),
    ])
    const icons = Array.from(container.querySelectorAll('svg[data-icon]')).map(e => e.getAttribute('data-icon'))
    expect(icons).toEqual(['fresh', 'rotten'])
    expect(screen.getByText('83%')).toBeTruthy()
  })

  it('treats an unknown look as plain and uses the declared logo as the mark', () => {
    const { container } = renderPanel([score({ look: 'sparkle' as never, logoUrl: '/api/v1/plugins/1/rating-logo?key=imdb' })])
    expect(container.querySelector('[data-look="sparkle"]')).not.toBeNull()
    expect(screen.getByText('8.2')).toBeTruthy()
  })

  it('shows the state the source reported in place of the tier icon, and draws the audience buckets', () => {
    const tiers = [{ min: 0, color: '#0AC855', icon: 'bucket-tipped' as const }, { min: 60, color: '#FA320A', icon: 'bucket-full' as const }]
    const { container } = renderPanel([
      score({ key: 'rt', displayName: 'Rotten Tomatoes', variant: 'critics', display: 'percent', value: 95, scale: 100, normalized: 95, look: 'icon-percent', state: 'certified',
        tiers: [{ min: 0, color: '#FA320A', icon: 'fresh' as const }] }),
      score({ key: 'rt', displayName: 'Rotten Tomatoes', variant: 'audience', display: 'percent', value: 90, scale: 100, normalized: 90, look: 'icon-percent', label: 'Popcornmeter', tiers }),
      score({ key: 'rt', displayName: 'Rotten Tomatoes', variant: 'other', display: 'percent', value: 20, scale: 100, normalized: 20, look: 'icon-percent', tiers }),
    ])
    expect(Array.from(container.querySelectorAll('svg[data-icon]')).map(e => e.getAttribute('data-icon'))).toEqual(['certified', 'bucket-full', 'bucket-tipped'])
  })

  it('writes a ring in the declared terms, not always as a percent', () => {
    const { container } = renderPanel([score({ look: 'ring', value: 8.2, scale: 10, normalized: 82, display: 'outOf' })])
    expect(container.querySelector('[class*="ringNumber"]')?.textContent).toBe('8.2/10')
  })

  it('a failed mark does not stick: a different address shows its image again', async () => {
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([])
    const { container, rerender } = render(
      <QueryClientProvider client={new QueryClient()}><ScoresPanel scores={[score({ logoUrl: '/a.png' })]} /></QueryClientProvider>,
    )
    fireEvent.error(container.querySelector('img')!)
    expect(container.querySelector('img')).toBeNull()
    rerender(<QueryClientProvider client={new QueryClient()}><ScoresPanel scores={[score({ logoUrl: '/b.png' })]} /></QueryClientProvider>)
    expect(container.querySelector('img')?.getAttribute('src')).toBe('/b.png')
  })
})
