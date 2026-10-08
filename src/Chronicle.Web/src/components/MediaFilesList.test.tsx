import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import MediaFilesList from './MediaFilesList'
import * as media from '@/api/media'

vi.mock('@/api/media')

function renderList(fallback: string | null = 'C:/m/Heat.mkv') {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={qc}><MediaFilesList mediaId={5} fallbackPath={fallback} /></QueryClientProvider>)
}

beforeEach(() => vi.mocked(media.getMediaFiles).mockReset())

describe('MediaFilesList', () => {
  it('lists every recorded file with its size, and flags the ones no longer on disk', async () => {
    vi.mocked(media.getMediaFiles).mockResolvedValue([
      { path: 'C:/m/Heat - Theatrical.mkv', type: 'file', exists: true, sizeBytes: 4 * 1024 * 1024 * 1024, modifiedUtc: null },
      { path: 'C:/m/Heat - Directors Cut.mkv', type: 'file', exists: false, sizeBytes: null, modifiedUtc: null },
      { path: 'C:/m/Album', type: 'folder', exists: true, sizeBytes: null, modifiedUtc: null },
    ])
    renderList()

    expect(await screen.findByText('C:/m/Heat - Theatrical.mkv')).toBeInTheDocument()
    expect(screen.getByText('4.00 GB')).toBeInTheDocument()
    expect(screen.getByText('not found on disk')).toBeInTheDocument()
    expect(screen.getByText('folder')).toBeInTheDocument()
    expect(screen.getAllByRole('listitem')).toHaveLength(3)
  })

  it('shows the item\'s own single path while nothing is recorded', async () => {
    vi.mocked(media.getMediaFiles).mockResolvedValue([])
    renderList('C:/m/Heat.mkv')

    expect(await screen.findByText('C:/m/Heat.mkv')).toBeInTheDocument()
    expect(screen.queryByRole('list')).not.toBeInTheDocument()
  })

  it('shows nothing at all when there is no path anywhere', async () => {
    vi.mocked(media.getMediaFiles).mockResolvedValue([])
    const { container } = renderList(null)

    await vi.waitFor(() => expect(media.getMediaFiles).toHaveBeenCalled())
    expect(container).toBeEmptyDOMElement()
  })
})
