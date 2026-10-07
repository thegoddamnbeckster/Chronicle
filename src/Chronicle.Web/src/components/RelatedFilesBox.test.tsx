import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import RelatedFilesBox from './RelatedFilesBox'
import * as media from '@/api/media'

vi.mock('@/api/media')

const row = (over: Partial<media.RelatedFile>): media.RelatedFile => ({
  id: 1, path: 'C:/Movies/Heat/Heat.en.srt', kind: 'subtitle', sizeBytes: 2048, discoveredAt: '2026-10-07T00:00:00Z', missingSince: null, ...over,
})

function renderBox() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={qc}><RelatedFilesBox mediaId={5} /></QueryClientProvider>)
}

beforeEach(() => vi.mocked(media.getRelatedFiles).mockReset())

describe('RelatedFilesBox', () => {
  it('shows nothing when the item has no related files', async () => {
    vi.mocked(media.getRelatedFiles).mockResolvedValue([])
    const { container } = renderBox()

    await vi.waitFor(() => expect(media.getRelatedFiles).toHaveBeenCalledWith(5))
    expect(container).toBeEmptyDOMElement()
  })

  it('groups the files by kind, shows just the file name and flags the ones that went missing', async () => {
    vi.mocked(media.getRelatedFiles).mockResolvedValue([
      row({}),
      row({ id: 2, path: 'C:/Movies/Heat/poster.jpg', kind: 'artwork', sizeBytes: 3 * 1024 * 1024, missingSince: '2026-10-08T00:00:00Z' }),
    ])
    renderBox()

    expect(await screen.findByText('Related files (2)')).toBeInTheDocument()
    expect(screen.getByText('Subtitles')).toBeInTheDocument()
    expect(screen.getByText('Artwork')).toBeInTheDocument()
    expect(screen.getByText('Heat.en.srt')).toBeInTheDocument()
    expect(screen.getByText('2 KB')).toBeInTheDocument()
    expect(screen.getByText('3.0 MB')).toBeInTheDocument()
    expect(screen.getByText('not found at last scan')).toBeInTheDocument()
  })
})
