import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import PluginsPage from './PluginsPage'
import { renderWithProviders } from '@/test/test-utils'
import * as pluginsApi from '@/api/plugins'
import * as importApi from '@/api/import'
import * as mediaApi from '@/api/media'
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
vi.mock('@/api/media')
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
  vi.mocked(pluginsApi.getCatalogSource).mockResolvedValue({ source: 'https://example.org/plugins.json', usingFallback: false, fetchedAtUtc: '2026-10-09T00:00:00Z', error: null, pluginCount: 1 })
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

describe('PluginsPage file integrity', () => {
  it('flags a plugin that was blocked, and approving its files replaces the card with the loaded one', async () => {
    const user = userEvent.setup()
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([{ ...IMDB, integrityBlockedAt: '2026-10-08T00:00:00Z', filesHash: 'aaaaaaaaaaaa' }])
    vi.mocked(pluginsApi.acceptPluginFiles).mockResolvedValue({ ...IMDB, integrityBlockedAt: null })
    renderWithProviders(<PluginsPage />)

    expect(await screen.findByText('Blocked: files changed')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Approve changed files' }))

    await vi.waitFor(() => expect(pluginsApi.acceptPluginFiles).toHaveBeenCalledWith('chronicle.plugin.imdb'))
    await vi.waitFor(() => expect(screen.queryByText('Blocked: files changed')).not.toBeInTheDocument())
  })

  it('does not approve anything when the confirmation is declined', async () => {
    const user = userEvent.setup()
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([{ ...IMDB, integrityBlockedAt: '2026-10-08T00:00:00Z' }])
    renderWithProviders(<PluginsPage />)

    await user.click(await screen.findByRole('button', { name: 'Approve changed files' }))

    expect(pluginsApi.acceptPluginFiles).not.toHaveBeenCalled()
  })

  it('shows that an update replaced the files, without any alarm', async () => {
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([{ ...IMDB, previousFilesHash: 'bbbbbbbbbbbb', filesHash: 'cccccccccccc', filesChangedAt: '2026-10-08T00:00:00Z' }])
    renderWithProviders(<PluginsPage />)

    expect(await screen.findByText(/Files changed/)).toBeInTheDocument()
    expect(screen.queryByText('Blocked: files changed')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Approve changed files' })).not.toBeInTheDocument()
  })

  it('shows nothing about file changes for a plugin whose files never changed', async () => {
    renderWithProviders(<PluginsPage />)

    await screen.findByText('IMDb')
    expect(screen.queryByText(/Files changed/)).not.toBeInTheDocument()
  })
})

describe('PluginsPage catalog filter', () => {
  it('narrows the catalog to the plugins that handle the chosen media type', async () => {
    const user = userEvent.setup()
    const entry = (id: string, name: string): pluginsApi.PluginCatalogEntry => ({
      pluginId: id, name, description: '', author: 'a', iconUrl: null, githubRepo: 'o/r', assetName: 'r.zip',
      dllName: 'r.dll', tags: [], isInstalled: false, version: '1.0.0',
    })
    vi.mocked(pluginsApi.listCatalog).mockImplementation(async (type?: string) =>
      type === 'music' ? [entry('mb', 'MusicBrainz')] : [entry('mb', 'MusicBrainz'), entry('tmdb', 'TMDB')])
    vi.mocked(mediaApi.getMediaTypes).mockResolvedValue([
      { id: 1, name: 'music', displayName: 'Music', hierarchyLevels: 3 },
      { id: 2, name: 'tv', displayName: 'TV', hierarchyLevels: 3 },
    ])
    renderWithProviders(<PluginsPage />)

    await user.click(await screen.findByRole('button', { name: /browse/i }))
    expect(await screen.findByText('TMDB')).toBeInTheDocument()

    await user.selectOptions(await screen.findByLabelText('Show plugins that handle'), 'music')

    await vi.waitFor(() => expect(pluginsApi.listCatalog).toHaveBeenLastCalledWith('music'))
    await vi.waitFor(() => expect(screen.queryByText('TMDB')).not.toBeInTheDocument())
    expect(screen.getByText('MusicBrainz')).toBeInTheDocument()
  })
})

describe('PluginsPage catalog source', () => {
  const entry: pluginsApi.PluginCatalogEntry = {
    pluginId: 'mb', name: 'MusicBrainz', description: '', author: 'a', iconUrl: null, githubRepo: 'o/r', assetName: 'r.zip',
    dllName: 'r.dll', tags: [], isInstalled: false, version: '1.0.0',
  }

  it('says where the catalog came from, and warns when the online copy could not be read', async () => {
    const user = userEvent.setup()
    vi.mocked(pluginsApi.listCatalog).mockResolvedValue([entry])
    vi.mocked(mediaApi.getMediaTypes).mockResolvedValue([])
    vi.mocked(pluginsApi.getCatalogSource).mockResolvedValue({
      source: 'built-in', usingFallback: true, fetchedAtUtc: '2026-10-09T00:00:00Z', error: '404', pluginCount: 15,
    })
    renderWithProviders(<PluginsPage />)

    await user.click(await screen.findByRole('button', { name: /browse/i }))

    expect(await screen.findByTestId('catalog-source')).toHaveTextContent('could not be reached')
    expect(screen.getByTestId('catalog-source')).toHaveTextContent('built-in')
  })

  it('re-reads the hosted catalog when asked', async () => {
    const user = userEvent.setup()
    vi.mocked(pluginsApi.listCatalog).mockResolvedValue([entry])
    vi.mocked(mediaApi.getMediaTypes).mockResolvedValue([])
    vi.mocked(pluginsApi.getCatalogSource).mockResolvedValue({
      source: 'https://example.org/plugins.json', usingFallback: false, fetchedAtUtc: '2026-10-09T00:00:00Z', error: null, pluginCount: 16,
    })
    vi.mocked(pluginsApi.refreshCatalogSource).mockResolvedValue({
      source: 'https://example.org/plugins.json', usingFallback: false, fetchedAtUtc: '2026-10-09T01:00:00Z', error: null, pluginCount: 17,
    })
    renderWithProviders(<PluginsPage />)
    await user.click(await screen.findByRole('button', { name: /browse/i }))
    expect(await screen.findByTestId('catalog-source')).toHaveTextContent('16 plugins')

    await user.click(screen.getByRole('button', { name: 'Refresh' }))

    await vi.waitFor(() => expect(pluginsApi.refreshCatalogSource).toHaveBeenCalled())
    expect(await screen.findByText(/17 plugins/)).toBeInTheDocument()
  })
})
