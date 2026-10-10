import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import GettingStartedPage, { rolesOf } from './GettingStartedPage'
import { renderWithProviders } from '@/test/test-utils'
import * as pluginsApi from '@/api/plugins'
import * as mediaApi from '@/api/media'

vi.mock('@/api/plugins', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/plugins')>()
  return {
    ...Object.fromEntries(Object.entries(actual).map(([k, v]) => [k, typeof v === 'function' ? vi.fn() : v])),
    SettingType: actual.SettingType,
  }
})
vi.mock('@/api/media')

const entry = (id: string, name: string, tags: string[], over: Partial<pluginsApi.PluginCatalogEntry> = {}): pluginsApi.PluginCatalogEntry => ({
  pluginId: id, name, description: `${name} description`, author: 'a', iconUrl: null, githubRepo: 'o/r', assetName: 'r.zip',
  dllName: 'r.dll', tags, isInstalled: false, version: '1.0.0', ...over,
})

const SCANNER = entry('scan', 'File Scanner', ['movies', 'tv', 'filescanner', 'local'])
const TMDB = entry('tmdb', 'TMDB', ['movies', 'tv', 'metadata'])
const MB = entry('mb', 'MusicBrainz', ['music', 'metadata'])
const TRAKT = entry('trakt', 'Trakt', ['movies', 'tv', 'scrobbling', 'sync'])
const THEME = entry('theme', 'Default Themes', ['themes', 'ui'])

const TYPES = [
  { id: 1, name: 'tv', displayName: 'TV Shows', hierarchyLevels: 3 },
  { id: 2, name: 'movies', displayName: 'Movies', hierarchyLevels: 1 },
  { id: 3, name: 'music', displayName: 'Music', hierarchyLevels: 3 },
]

beforeEach(() => {
  vi.mocked(mediaApi.getMediaTypes).mockResolvedValue(TYPES)
  vi.mocked(pluginsApi.listPlugins).mockResolvedValue([])
  vi.mocked(pluginsApi.listCatalog).mockImplementation(async (type?: string) => {
    if (type === 'music') return [SCANNER, MB]
    return [SCANNER, TMDB, TRAKT, THEME]
  })
})

describe('rolesOf', () => {
  it.each([
    [['filescanner', 'movies'], ['scanner']],
    [['movies', 'metadata'], ['metadata']],
    [['artwork'], ['metadata']],
    [['movies', 'scrobbling', 'sync'], ['tracking']],
    [['themes', 'ui'], ['appearance']],
    [['anything else'], ['other']],
    [[], ['other']],
  ])('%j -> %j', (tags, expected) => {
    expect(rolesOf({ tags })).toEqual(expected)
  })
})

describe('GettingStartedPage', () => {
  it('says plainly that Chronicle has no plugins and walks through in order', async () => {
    renderWithProviders(<GettingStartedPage />)

    expect(await screen.findByRole('heading', { name: 'Getting started' })).toBeInTheDocument()
    expect(screen.getByText(/ships with no plugins/)).toBeInTheDocument()
    const steps = screen.getAllByRole('heading', { level: 2 }).map(h => h.textContent)
    expect(steps[0]).toContain('What do you keep?')
    expect(steps.join('|')).toContain('Let Chronicle find your files')
    expect(steps.join('|')).toContain('Look up information')
    expect(steps.join('|')).toContain('Point Chronicle at your folders')
  })

  it('offers the scanner and the information sources that fit what was ticked, and the optional extras apart', async () => {
    renderWithProviders(<GettingStartedPage />)

    expect(await screen.findByRole('button', { name: 'Install File Scanner' })).toBeInTheDocument()
    expect(await screen.findByRole('button', { name: 'Install TMDB' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Install MusicBrainz' })).toBeInTheDocument()
    // The scanner is only listed once, not once per ticked type; extras sit in their own folded sections.
    expect(screen.getAllByRole('button', { name: 'Install File Scanner' })).toHaveLength(1)
    expect(screen.getByText('Keep your history in sync with other services')).toBeInTheDocument()
    expect(screen.getByText('Change how Chronicle looks')).toBeInTheDocument()
  })

  it('narrows the suggestions to the media you keep', async () => {
    const user = userEvent.setup()
    renderWithProviders(<GettingStartedPage />)
    await screen.findByRole('button', { name: 'Install MusicBrainz' })

    await user.click(screen.getByRole('checkbox', { name: 'TV Shows' }))
    await user.click(screen.getByRole('checkbox', { name: 'Movies' }))

    await waitFor(() => expect(screen.queryByRole('button', { name: 'Install TMDB' })).not.toBeInTheDocument())
    expect(screen.getByRole('button', { name: 'Install MusicBrainz' })).toBeInTheDocument()
  })

  it('installs a plugin from the catalog and marks it installed', async () => {
    const user = userEvent.setup()
    vi.mocked(pluginsApi.installFromCatalog).mockResolvedValue({ id: 5, pluginId: 'scan', name: 'File Scanner' } as pluginsApi.PluginDto)
    renderWithProviders(<GettingStartedPage />)

    await user.click(await screen.findByRole('button', { name: 'Install File Scanner' }))

    await waitFor(() => expect(pluginsApi.installFromCatalog).toHaveBeenCalledWith('scan'))
    const card = screen.getAllByText('File Scanner description')[0].closest('li')!
    expect(await within(card).findByText('Installed')).toBeInTheDocument()
  })

  it('shows why an install failed instead of pretending it worked', async () => {
    const user = userEvent.setup()
    vi.mocked(pluginsApi.installFromCatalog).mockRejectedValue(new Error('No installable release found'))
    renderWithProviders(<GettingStartedPage />)

    await user.click(await screen.findByRole('button', { name: 'Install TMDB' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No installable release found')
    expect(screen.getByRole('button', { name: 'Install TMDB' })).toBeInTheDocument()
  })

  it('asks for the key an installed plugin needs, saves it and checks the plugin works', async () => {
    const user = userEvent.setup()
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([{ id: 9, pluginId: 'tmdb', name: 'TMDB', isEnabled: true } as pluginsApi.PluginDto])
    vi.mocked(pluginsApi.getPluginSettingsSchema).mockResolvedValue({
      settings: [
        { key: 'api_key', label: 'API key', type: pluginsApi.SettingType.Password, required: true, description: 'From your TMDB account.' },
        { key: 'language', label: 'Language', type: pluginsApi.SettingType.Text, required: false },
      ],
    })
    vi.mocked(pluginsApi.getPluginSettings).mockResolvedValue({})
    vi.mocked(pluginsApi.updatePluginSettings).mockResolvedValue(undefined)
    vi.mocked(pluginsApi.healthCheckPlugin).mockResolvedValue({ healthy: true } as pluginsApi.PluginHealthResult)
    renderWithProviders(<GettingStartedPage />)

    expect(await screen.findByText(/needs one more thing/)).toBeInTheDocument()
    expect(screen.queryByLabelText(/Language/)).not.toBeInTheDocument()   // optional settings are left for later
    const save = screen.getByRole('button', { name: 'Save and test' })
    expect(save).toBeDisabled()

    await user.type(screen.getByLabelText(/API key/), 'abc123')
    await user.click(save)

    await waitFor(() => expect(pluginsApi.updatePluginSettings).toHaveBeenCalledWith(9, { api_key: 'abc123' }))
    expect(await screen.findByText('TMDB is working.')).toBeInTheDocument()
  })

  it('says so when the key does not pass the plugin check', async () => {
    const user = userEvent.setup()
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([{ id: 9, pluginId: 'tmdb', name: 'TMDB', isEnabled: true } as pluginsApi.PluginDto])
    vi.mocked(pluginsApi.getPluginSettingsSchema).mockResolvedValue({
      settings: [{ key: 'api_key', label: 'API key', type: pluginsApi.SettingType.Password, required: true }],
    })
    vi.mocked(pluginsApi.getPluginSettings).mockResolvedValue({})
    vi.mocked(pluginsApi.updatePluginSettings).mockResolvedValue(undefined)
    vi.mocked(pluginsApi.healthCheckPlugin).mockResolvedValue({ healthy: false, failureReason: 'Invalid API key' } as pluginsApi.PluginHealthResult)
    renderWithProviders(<GettingStartedPage />)

    await user.type(await screen.findByLabelText(/API key/), 'wrong')
    await user.click(screen.getByRole('button', { name: 'Save and test' }))

    expect(await screen.findByText(/did not pass its check: Invalid API key/)).toBeInTheDocument()
  })

  it('lists nothing to configure when every installed plugin is ready', async () => {
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([{ id: 9, pluginId: 'tmdb', name: 'TMDB', isEnabled: true } as pluginsApi.PluginDto])
    vi.mocked(pluginsApi.getPluginSettingsSchema).mockResolvedValue({
      settings: [{ key: 'api_key', label: 'API key', type: pluginsApi.SettingType.Password, required: true }],
    })
    vi.mocked(pluginsApi.getPluginSettings).mockResolvedValue({ api_key: 'already' })
    renderWithProviders(<GettingStartedPage />)

    await screen.findByText(/Nothing listed above means everything you installed is ready/)
    await waitFor(() => expect(pluginsApi.getPluginSettingsSchema).toHaveBeenCalled())
    expect(screen.queryByText(/needs one more thing/)).not.toBeInTheDocument()
  })

  it('tells you when the catalog cannot be read', async () => {
    vi.mocked(pluginsApi.listCatalog).mockRejectedValue(new Error('offline'))
    renderWithProviders(<GettingStartedPage />)

    expect(await screen.findByRole('alert')).toHaveTextContent('The plugin catalog could not be read')
  })

  it('ends by pointing at the scan page', async () => {
    renderWithProviders(<GettingStartedPage />)

    expect(await screen.findByRole('link', { name: 'Go to Scan' })).toHaveAttribute('href', '/scan')
  })
})
