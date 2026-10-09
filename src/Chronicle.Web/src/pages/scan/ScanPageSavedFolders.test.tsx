import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import ScanPage from './ScanPage'
import { renderWithProviders } from '@/test/test-utils'
import { BackgroundActivityProvider } from '@/contexts/BackgroundActivityContext'
import * as scanApi from '@/api/scan'
import * as mediaApi from '@/api/media'
import type { ScanFolder } from '@/types'

vi.mock('@/api/scan')
vi.mock('@/api/media')
vi.mock('@/components/PathInput', () => ({
  default: ({ value, onChange, onBlur, placeholder }: { value: string; onChange: (v: string) => void; onBlur?: () => void; placeholder?: string }) => (
    <input aria-label="Folder path" value={value} placeholder={placeholder} onChange={e => onChange(e.target.value)} onBlur={onBlur} />
  ),
}))

const folder = (over: Partial<ScanFolder> = {}): ScanFolder => ({
  id: 1, path: 'D:\\Video\\Movies', mediaTypeId: 2, mediaTypeName: 'Movies', recursive: true, isEnabled: true,
  createdAt: '2026-10-01T00:00:00Z', lastScannedAt: null, bundleRelatedFiles: null, ...over,
})

function renderPage() {
  return renderWithProviders(<BackgroundActivityProvider><ScanPage /></BackgroundActivityProvider>)
}

beforeEach(() => {
  vi.mocked(scanApi.getScanStatus).mockResolvedValue({ available: true, supportedMediaTypeNames: ['movies', 'tv'] })
  vi.mocked(mediaApi.getMediaTypes).mockResolvedValue([
    { id: 1, name: 'tv', displayName: 'TV Shows', hierarchyLevels: 3 },
    { id: 2, name: 'movies', displayName: 'Movies', hierarchyLevels: 1 },
  ])
  vi.mocked(scanApi.getScanFolders).mockResolvedValue([])
  vi.mocked(scanApi.validatePath).mockResolvedValue({ valid: true, error: null })
  vi.mocked(scanApi.createScanFolder).mockResolvedValue(folder())
  vi.mocked(scanApi.updateScanFolder).mockResolvedValue(folder())
})

describe('Saved scan folders', () => {
  it('saves a folder that sorts each file into its own type, remembering related files only when told to', async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole('button', { name: '+ Add folder' }))
    await user.type(screen.getAllByLabelText('Folder path')[0], 'E:\\Downloads')
    await user.tab()
    await waitFor(() => expect(scanApi.validatePath).toHaveBeenCalled())
    const row = screen.getAllByLabelText('Folder path')[0].closest('div')!.parentElement!
    const selects = within(row).getAllByRole('combobox')
    await user.selectOptions(selects[0], 'Detect automatically')
    await user.selectOptions(within(row).getByLabelText('Related files'), 'always')
    await user.click(within(row).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(scanApi.createScanFolder).toHaveBeenCalledWith({
      path: 'E:\\Downloads', mediaTypeId: 0, recursive: true, bundleRelatedFiles: true,
    }))
  })

  it('a new folder follows the global related-files setting unless changed', async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole('button', { name: '+ Add folder' }))
    await user.type(screen.getAllByLabelText('Folder path')[0], 'D:\\Video\\Movies')
    await user.tab()
    await waitFor(() => expect(scanApi.validatePath).toHaveBeenCalled())
    const row = screen.getAllByLabelText('Folder path')[0].closest('div')!.parentElement!
    await user.selectOptions(within(row).getAllByRole('combobox')[0], 'Movies')
    await user.click(within(row).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(scanApi.createScanFolder).toHaveBeenCalledWith({
      path: 'D:\\Video\\Movies', mediaTypeId: 2, recursive: true, bundleRelatedFiles: null,
    }))
  })

  it('shows an automatic folder by name and edits it without losing the choice', async () => {
    const user = userEvent.setup()
    vi.mocked(scanApi.getScanFolders).mockResolvedValue([
      folder({ id: 7, path: 'E:\\Downloads', mediaTypeId: null, mediaTypeName: 'Detect automatically', bundleRelatedFiles: false }),
    ])
    renderPage()

    expect(await screen.findByText('Detect automatically', { selector: 'span,div,small,em' })).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Edit' }))
    expect(screen.getAllByRole('combobox')[0]).toHaveValue('0')
    expect(screen.getByLabelText('Related files')).toHaveValue('never')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(scanApi.updateScanFolder).toHaveBeenCalledWith(7, expect.objectContaining({
      mediaTypeId: 0, bundleRelatedFiles: false,
    })))
  })

  it('switching a folder on or off keeps its automatic type and related-files choice', async () => {
    const user = userEvent.setup()
    vi.mocked(scanApi.getScanFolders).mockResolvedValue([
      folder({ id: 7, path: 'E:\\Downloads', mediaTypeId: null, mediaTypeName: 'Detect automatically', bundleRelatedFiles: true }),
    ])
    renderPage()

    await user.click(await screen.findByRole('checkbox', { name: /Enabled/ }))

    await waitFor(() => expect(scanApi.updateScanFolder).toHaveBeenCalledWith(7, {
      path: 'E:\\Downloads', mediaTypeId: 0, recursive: true, isEnabled: false, bundleRelatedFiles: true,
    }))
  })

  it('"Scan Now" on an automatic folder starts a detect-automatically scan', async () => {
    const user = userEvent.setup()
    vi.mocked(scanApi.getScanFolders).mockResolvedValue([
      folder({ id: 7, path: 'E:\\Downloads', mediaTypeId: null, mediaTypeName: 'Detect automatically' }),
    ])
    vi.mocked(scanApi.previewGrouped).mockResolvedValue({ groups: [], ungrouped: [], totalFiles: 0, totalGroups: 0 })
    renderPage()

    await user.click(await screen.findByRole('button', { name: 'Scan Now' }))
    await user.click(await screen.findByRole('button', { name: 'Scan Directory' }))

    await waitFor(() => expect(scanApi.previewGrouped).toHaveBeenCalledWith({ path: 'E:\\Downloads', recursive: true, mediaTypeId: 0 }))
  })
})
