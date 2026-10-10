import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import ApiKeysPage from './ApiKeysPage'
import * as tokens from '@/api/apiTokens'

vi.mock('@/api/apiTokens')

const SCOPES: tokens.ApiKeyScopeInfo[] = [
  { scope: 'full', description: 'Full access - everything the owning account can do.' },
  { scope: 'device', description: 'Kodi / scrobblers - scrobbling and the Kodi scraper.' },
  { scope: 'bridge', description: 'Audiobookshelf metadata bridge.' },
]

const KEY: tokens.ApiTokenDto = {
  id: 7, name: 'Old Kodi key', createdAt: '2026-01-01T00:00:00Z', lastUsedAt: null, expiresAt: null, scope: 'full',
}

beforeEach(() => {
  vi.mocked(tokens.listApiKeyScopes).mockResolvedValue(SCOPES)
  vi.mocked(tokens.listApiTokens).mockResolvedValue([KEY])
})

describe('ApiKeysPage scopes', () => {
  it('defaults a new key to the least-privileged scope that fits the usual use', async () => {
    vi.mocked(tokens.createApiToken).mockResolvedValue({
      id: 8, name: 'Living room', token: 'chr_live_x', createdAt: '2026-10-07T00:00:00Z', expiresAt: null, scope: 'device',
    })
    render(<ApiKeysPage />)

    await userEvent.type(await screen.findByLabelText('Name'), 'Living room')
    await userEvent.click(screen.getByRole('button', { name: 'Create Key' }))

    await waitFor(() => expect(tokens.createApiToken).toHaveBeenCalledWith('Living room', null, 'device'))
  })

  it('sends the scope the user picked', async () => {
    vi.mocked(tokens.createApiToken).mockResolvedValue({
      id: 9, name: 'ABS', token: 'chr_live_y', createdAt: '2026-10-07T00:00:00Z', expiresAt: null, scope: 'bridge',
    })
    render(<ApiKeysPage />)

    await userEvent.type(await screen.findByLabelText('Name'), 'ABS')
    await userEvent.selectOptions(screen.getByLabelText('Access'), 'bridge')
    await userEvent.click(screen.getByRole('button', { name: 'Create Key' }))

    await waitFor(() => expect(tokens.createApiToken).toHaveBeenCalledWith('ABS', null, 'bridge'))
  })

  it('flags an existing full-access key and lets it be narrowed', async () => {
    vi.mocked(tokens.setApiTokenScope).mockResolvedValue()
    render(<ApiKeysPage />)

    expect(await screen.findByText(/Full access - restrict it/)).toBeInTheDocument()
    await userEvent.selectOptions(screen.getByLabelText('Access for Old Kodi key'), 'device')

    await waitFor(() => expect(tokens.setApiTokenScope).toHaveBeenCalledWith(7, 'device'))
    await waitFor(() => expect(screen.queryByText(/Full access - restrict it/)).not.toBeInTheDocument())
  })

  it('shows the description of the selected scope', async () => {
    render(<ApiKeysPage />)

    expect(await screen.findByText(/Kodi \/ scrobblers - scrobbling and the Kodi scraper/)).toBeInTheDocument()
  })
})
