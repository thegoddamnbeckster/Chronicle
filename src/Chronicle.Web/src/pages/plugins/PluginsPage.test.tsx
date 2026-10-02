import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import PluginsPage from './PluginsPage'
import { renderWithProviders } from '@/test/test-utils'
import * as pluginsApi from '@/api/plugins'
import * as importApi from '@/api/import'
import { useAuth } from '@/hooks/useAuth'
import { useTheme } from '@/contexts/ThemeContext'
import type { User } from '@/types'

vi.mock('@/api/plugins', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/plugins')>()
  // Keep the real SettingType values; mock every request.
  return {
    ...Object.fromEntries(Object.entries(actual).map(([k, v]) => [k, typeof v === 'function' ? vi.fn() : v])),
    SettingType: actual.SettingType,
  }
})
vi.mock('@/api/import')
vi.mock('@/hooks/useAuth')
vi.mock('@/contexts/ThemeContext')

const ADMIN: User = {
  id: 1, username: 'admin', email: 'admin@example.com', displayName: 'Admin', isAdmin: true,
  showDiagnostics: false, showNowPlayingBanner: true, showAllCredits: false,
}

const IMDB: pluginsApi.PluginDto = {
  id: 28, pluginId: 'chronicle.plugin.imdb', name: 'IMDb', version: '1.0.0', author: 'Chronicle Contributors',
  description: null, isEnabled: true, installedAt: '2026-10-02T00:00:00Z', updatedAt: '2026-10-02T00:00:00Z',
  iconUrl: null, fixMatchHint: null, supportedMediaTypes: ['Movies'], latestVersionAvailable: null,
  updateCheckedAt: null,
}

beforeEach(() => {
  vi.mocked(useAuth).mockReturnValue({ user: ADMIN, loading: false, logout: vi.fn(), setUser: vi.fn() })
  vi.mocked(useTheme).mockReturnValue({ themes: [], activeKey: '', loading: false, setTheme: vi.fn() })
  vi.mocked(importApi.getImportProviders).mockResolvedValue([])
  vi.mocked(pluginsApi.listPlugins).mockResolvedValue([IMDB])
  vi.mocked(pluginsApi.healthCheckPlugin).mockResolvedValue({ healthy: true } as pluginsApi.PluginHealthResult)
  vi.mocked(pluginsApi.getPluginSettings).mockResolvedValue({})
  vi.mocked(pluginsApi.updatePluginSettings).mockResolvedValue(undefined)
  vi.mocked(pluginsApi.getPluginSettingsSchema).mockResolvedValue({
    settings: [
      { key: 'disk_space_notice', label: 'Disk space', type: pluginsApi.SettingType.Notice, required: false,
        description: 'This plugin builds a local index of about 10 GB.' },
      { key: 'include_adult', label: 'Include adult titles', type: pluginsApi.SettingType.Boolean, required: false,
        description: 'Index adult titles.', defaultValue: 'true' },
    ],
  })
})

describe('PluginsPage settings', () => {
  it('shows a Notice setting as a callout, not an input, and never saves it', async () => {
    const user = userEvent.setup()
    renderWithProviders(<PluginsPage />)

    await user.click(await screen.findByRole('button', { name: 'Configure' }))

    const notice = await screen.findByRole('note')
    expect(notice).toHaveTextContent('Disk space')
    expect(notice).toHaveTextContent('This plugin builds a local index of about 10 GB.')
    expect(notice.querySelector('input, textarea, select')).toBeNull()
    expect(screen.getByRole('checkbox')).toBeChecked() // the ordinary setting still renders

    await user.click(screen.getByRole('button', { name: /save/i }))
    expect(pluginsApi.updatePluginSettings).toHaveBeenCalledWith(28, { include_adult: 'true' })
  })
})
