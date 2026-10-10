import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import MediaTypesPage from './MediaTypesPage'
import * as api from '@/api/mediaTypes'

vi.mock('@/api/mediaTypes', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/mediaTypes')>()
  return { ...actual, listMediaTypesAdmin: vi.fn(), createMediaType: vi.fn(), updateMediaType: vi.fn(), releaseMediaType: vi.fn(), deleteMediaType: vi.fn() }
})

const TV: api.MediaTypeAdmin = {
  id: 1, name: 'tv', displayName: 'TV Shows', description: null, hierarchyLevels: 3, hierarchyLabels: ['Show', 'Season', 'Episode'],
  interactionVerb: 'watched', progressUnit: 'minutes', isBuiltIn: true, isActive: true, supportsCollections: false, isTrackable: true,
  scanStrategy: null, isUserModified: false, itemCount: 120, plugins: [{ pluginId: 'p.tmdb', name: 'TMDB' }], scanHints: null, providerFamily: 'tv', castHeading: null,
}
const COMICS: api.MediaTypeAdmin = {
  id: 9, name: 'comics', displayName: 'Comics', description: 'Comic books', hierarchyLevels: 3, hierarchyLabels: ['Series', 'Volume', 'Issue'],
  interactionVerb: 'read', progressUnit: 'pages', isBuiltIn: false, isActive: true, supportsCollections: false, isTrackable: true,
  scanStrategy: null, isUserModified: true, itemCount: 0, plugins: [], scanHints: '{"extensions":[".cbz"]}', providerFamily: null, castHeading: null,
}

const renderPage = () => render(<MemoryRouter><MediaTypesPage /></MemoryRouter>)

beforeEach(() => {
  vi.mocked(api.listMediaTypesAdmin).mockResolvedValue([TV, COMICS])
})

describe('MediaTypesPage', () => {
  it('lists each type with its level names, action word, item count and the plugins that handle it', async () => {
    renderPage()

    expect(await screen.findByText(/Show > Season > Episode · watched · 120 items · handled by TMDB/)).toBeInTheDocument()
    expect(screen.getByText(/Series > Volume > Issue · read · 0 items · no installed plugin handles it · edited by you/)).toBeInTheDocument()
  })

  it('points to the plugin catalogue for an empty type nothing handles', async () => {
    renderPage()

    const hint = await screen.findByText(/until a plugin for it is installed/)
    expect(within(hint).getByRole('link', { name: 'browse plugins' })).toHaveAttribute('href', '/plugins')
  })

  it('offers delete only for types you made, after confirmation', async () => {
    vi.mocked(api.deleteMediaType).mockResolvedValue()
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    renderPage()
    await screen.findByText(/Comics/, { selector: 'span' })
    expect(screen.getAllByRole('button', { name: 'Delete' })).toHaveLength(1)

    await userEvent.click(screen.getByRole('button', { name: 'Delete' }))
    expect(api.deleteMediaType).not.toHaveBeenCalled()

    confirm.mockReturnValue(true)
    await userEvent.click(screen.getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(api.deleteMediaType).toHaveBeenCalledWith(9))
  })

  it('adds a type from what is typed, turning the level names into a list', async () => {
    vi.mocked(api.createMediaType).mockResolvedValue({ ...COMICS, id: 10 })
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Add a type' }))
    const dialog = screen.getByRole('dialog', { name: 'Add a media type' })

    await userEvent.type(within(dialog).getByLabelText('Display name'), 'Board Games')
    await userEvent.type(within(dialog).getByLabelText('Internal name'), 'board-games')
    await userEvent.clear(within(dialog).getByLabelText('Levels'))
    await userEvent.type(within(dialog).getByLabelText('Levels'), '2')
    await userEvent.type(within(dialog).getByLabelText(/Level names/), 'Game, Expansion')
    await userEvent.clear(within(dialog).getByLabelText(/Action word/))
    await userEvent.type(within(dialog).getByLabelText(/Action word/), 'played')
    await userEvent.clear(within(dialog).getByLabelText('Progress unit'))
    await userEvent.type(within(dialog).getByLabelText('Progress unit'), 'sessions')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(api.createMediaType).toHaveBeenCalledWith({
      name: 'board-games', displayName: 'Board Games', description: '', hierarchyLevels: 2, hierarchyLabels: ['Game', 'Expansion'],
      interactionVerb: 'played', progressUnit: 'sessions', supportsCollections: false, isTrackable: true, scanStrategy: null, isActive: true, scanHints: '', providerFamily: '', castHeading: '',
    }))
    expect(await screen.findByText('Added Board Games.')).toBeInTheDocument()
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('keeps the form open and shows the reason when the server refuses', async () => {
    vi.mocked(api.createMediaType).mockRejectedValue(new Error('A media type named \'tv\' already exists.'))
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Add a type' }))
    const dialog = screen.getByRole('dialog')
    await userEvent.type(within(dialog).getByLabelText('Display name'), 'Again')
    await userEvent.type(within(dialog).getByLabelText('Internal name'), 'tv')

    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('already exists')
    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('edits a type: the internal name is fixed, and the level count is fixed while it holds items', async () => {
    vi.mocked(api.updateMediaType).mockResolvedValue(TV)
    renderPage()
    const row = (await screen.findByText(/TV Shows/, { selector: 'span' })).closest('div')!.parentElement!
    await userEvent.click(within(row).getByRole('button', { name: 'Edit' }))
    const dialog = screen.getByRole('dialog', { name: 'Edit media type' })

    expect(within(dialog).getByLabelText('Internal name')).toBeDisabled()
    expect(within(dialog).getByLabelText('Internal name')).toHaveValue('tv')
    expect(within(dialog).getByLabelText('Levels')).toBeDisabled()
    expect(dialog).toHaveTextContent('Fixed while this type holds items')

    await userEvent.clear(within(dialog).getByLabelText('Display name'))
    await userEvent.type(within(dialog).getByLabelText('Display name'), 'Television')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(api.updateMediaType).toHaveBeenCalledWith(1, expect.objectContaining({ displayName: 'Television', hierarchyLevels: 3 })))
    expect(vi.mocked(api.updateMediaType).mock.calls[0][1]).not.toHaveProperty('name')
  })

  it('shows the scan hints of a type for editing and sends them back (an emptied box clears them)', async () => {
    vi.mocked(api.updateMediaType).mockResolvedValue(COMICS)
    renderPage()
    const row = (await screen.findByText(/Comics/, { selector: 'span' })).closest('div')!.parentElement!
    await userEvent.click(within(row).getByRole('button', { name: 'Edit' }))
    const dialog = screen.getByRole('dialog', { name: 'Edit media type' })
    const hints = within(dialog).getByLabelText('What its files look like (optional)')
    expect(hints).toHaveValue('{"extensions":[".cbz"]}')

    await userEvent.clear(hints)
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(api.updateMediaType).toHaveBeenCalledWith(9, expect.objectContaining({ scanHints: '' })))
  })

  it('edits the provider family and the heading for credited people', async () => {
    vi.mocked(api.updateMediaType).mockResolvedValue(TV)
    renderPage()
    const row = (await screen.findByText(/TV Shows/, { selector: 'span' })).closest('div')!.parentElement!
    await userEvent.click(within(row).getByRole('button', { name: 'Edit' }))
    const dialog = screen.getByRole('dialog', { name: 'Edit media type' })
    expect(within(dialog).getByLabelText('Also served by providers for')).toHaveValue('tv')

    await userEvent.clear(within(dialog).getByLabelText('Also served by providers for'))
    await userEvent.type(within(dialog).getByLabelText('Also served by providers for'), 'Movie')
    await userEvent.type(within(dialog).getByLabelText('Heading for credited people'), 'Starring')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(api.updateMediaType).toHaveBeenCalledWith(1, expect.objectContaining({ providerFamily: 'movie', castHeading: 'Starring' })))
  })

  it('warns when the chosen action word is one Chronicle has no special wording for', async () => {
    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Add a type' }))
    const dialog = screen.getByRole('dialog')

    await userEvent.clear(within(dialog).getByLabelText(/Action word/))
    await userEvent.type(within(dialog).getByLabelText(/Action word/), 'tasted')

    expect(dialog).toHaveTextContent('no special wording')
    await userEvent.clear(within(dialog).getByLabelText(/Action word/))
    await userEvent.type(within(dialog).getByLabelText(/Action word/), 'listened')
    expect(dialog).not.toHaveTextContent('no special wording')
  })

  it('can hand an edited type back to its plugins, but only when a plugin handles it', async () => {
    vi.mocked(api.releaseMediaType).mockResolvedValue(TV)
    vi.mocked(api.listMediaTypesAdmin).mockResolvedValue([{ ...TV, isUserModified: true }, COMICS])
    renderPage()

    const buttons = await screen.findAllByRole('button', { name: 'Hand back to plugins' })
    expect(buttons).toHaveLength(1)   // comics is edited too, but nothing handles it
    await userEvent.click(buttons[0])

    await waitFor(() => expect(api.releaseMediaType).toHaveBeenCalledWith(1))
  })

  it('shows a load failure instead of an empty page', async () => {
    vi.mocked(api.listMediaTypesAdmin).mockRejectedValue(new Error('boom'))
    renderPage()

    expect(await screen.findByText('boom')).toBeInTheDocument()
  })
})
