import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import ScanSettingsSection from './ScanSettingsSection'
import * as settings from '@/api/settings'

vi.mock('@/api/settings')

function renderSection(rows: Record<string, string> = {}) {
  vi.mocked(settings.getAppSettings).mockResolvedValue(rows)
  vi.mocked(settings.putAppSetting).mockResolvedValue()
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(<QueryClientProvider client={qc}><ScanSettingsSection /></QueryClientProvider>)
}

beforeEach(() => vi.resetAllMocks())

describe('ScanSettingsSection', () => {
  it('defaults to related files off and holding back suspicious folders', async () => {
    renderSection()

    expect(await screen.findByRole('switch', { name: 'Remember related files' })).toHaveAttribute('aria-checked', 'false')
    expect(screen.getByLabelText('When files look like a different kind of media')).toHaveValue('flag')
  })

  it('turns related-file bundling on', async () => {
    renderSection()

    await userEvent.click(await screen.findByRole('switch', { name: 'Remember related files' }))

    await waitFor(() => expect(settings.putAppSetting).toHaveBeenCalledWith('scan.bundle_related_files', 'true'))
  })

  it('switches the mismatch action', async () => {
    renderSection()

    await userEvent.selectOptions(await screen.findByLabelText('When files look like a different kind of media'), 'ignore')

    await waitFor(() => expect(settings.putAppSetting).toHaveBeenCalledWith('scan.mismatch_action', 'ignore'))
  })

  it('shows the saved lists and saves an edited one', async () => {
    renderSection({ 'scan.sidecar_extensions': '.srt, .vtt', 'scan.mismatch_action': 'ignore' })
    const ext = await screen.findByLabelText('Extensions')
    await waitFor(() => expect(ext).toHaveValue('.srt, .vtt'))
    expect(screen.getByLabelText('When files look like a different kind of media')).toHaveValue('ignore')

    await userEvent.clear(ext)
    await userEvent.type(ext, '.ass')
    await userEvent.click(screen.getByRole('button', { name: 'Save extensions' }))

    await waitFor(() => expect(settings.putAppSetting).toHaveBeenCalledWith('scan.sidecar_extensions', '.ass'))
  })
})
