import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes, useLocation } from 'react-router-dom'
import EnrichmentDrillDownPage from './EnrichmentDrillDownPage'
import { renderWithProviders } from '@/test/test-utils'
import * as enrichmentApi from '@/api/enrichment'
import type { EnrichmentItem } from '@/api/enrichment'

vi.mock('@/api/enrichment')
vi.mock('@/api/media')

function makeItem(overrides: Partial<EnrichmentItem> & { mediaItemId: number; name: string }): EnrichmentItem {
  return {
    enrichmentId: overrides.mediaItemId,
    year: null,
    mediaType: 'music',
    hierarchyLevel: 0,
    posterUrl: null,
    externalId: null,
    status: 'Completed',
    errorMessage: null,
    retryCount: 0,
    maxRetries: 3,
    lastAttemptedAt: null,
    diagnostics: null,
    fileScannerMetadata: null,
    parentName: null,
    ...overrides,
  } as EnrichmentItem
}

function Where() {
  const loc = useLocation()
  return <div data-testid="where">{loc.pathname}{loc.hash}</div>
}

function renderPage(item: EnrichmentItem) {
  vi.mocked(enrichmentApi.getEnrichmentItems).mockResolvedValue({ items: [item], total: 1, page: 1, pageSize: 25, totalPages: 1 })
  vi.mocked(enrichmentApi.getEnrichmentStats).mockResolvedValue([])
  renderWithProviders(
    <Routes>
      <Route path="/settings/enrichment/:pluginId" element={<EnrichmentDrillDownPage />} />
      <Route path="*" element={<Where />} />
    </Routes>,
    { initialEntries: ['/settings/enrichment/chronicle.plugin.test'] },
  )
}

beforeEach(() => vi.clearAllMocks())

/** The library grid only lists top-level items, so "View in Library" for anything deeper
 * (an album, a track, a season, an episode) used to land on the top of the library. */
describe('EnrichmentDrillDownPage — View in Library', () => {
  it('opens a deeper item on its own page, where the breadcrumb shows its place in the hierarchy', async () => {
    renderPage(makeItem({ mediaItemId: 4242, name: '#36', hierarchyLevel: 2 }))
    await userEvent.click(await screen.findByRole('button', { name: /View in Library/ }))
    expect(await screen.findByTestId('where')).toHaveTextContent('/media/4242')
  })

  it('still scrolls the library to a top-level item', async () => {
    renderPage(makeItem({ mediaItemId: 7, name: 'Radiohead', hierarchyLevel: 0 }))
    await userEvent.click(await screen.findByRole('button', { name: /View in Library/ }))
    expect(await screen.findByTestId('where')).toHaveTextContent('/library#media-7')
  })
})
