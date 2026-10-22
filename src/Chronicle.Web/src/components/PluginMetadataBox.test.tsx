import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { PluginMetadataBox } from './PluginMetadataBox'
import { renderWithProviders } from '@/test/test-utils'
import type { MediaItem } from '@/types'
import * as mediaApi from '@/api/media'

vi.mock('@/api/media')

const CONFLICT = { itemId: 505711, itemName: 'Shemer Kuznits', mediaTypeName: 'People' }

function renderBox(props: Partial<React.ComponentProps<typeof PluginMetadataBox>> = {}) {
  return renderWithProviders(
    <PluginMetadataBox
      mediaId={386766}
      pluginId="chronicle.plugin.openlibrary"
      pluginName="Open Library"
      externalIds={[]}
      {...props}
    />,
  )
}

/** The item as the server returns it after a Fix Match: refused (NotFound) with the owner named, or matched. */
function afterFixMatch(refused: boolean): MediaItem {
  return {
    id: 386766,
    enrichmentStatuses: { 'chronicle.plugin.openlibrary': refused ? 'NotFound' : 'Completed' },
    enrichmentConflicts: refused ? { 'chronicle.plugin.openlibrary': CONFLICT } : undefined,
  } as unknown as MediaItem
}

beforeEach(() => vi.clearAllMocks())

describe('PluginMetadataBox: a match refused because the item already exists in Chronicle', () => {
  it('says the entry is already in Chronicle, and links to it, instead of a bare "no match"', () => {
    renderBox({ enrichmentStatus: 'NotFound', enrichmentConflict: CONFLICT })

    expect(screen.getByText(/already in chronicle as/i)).toBeInTheDocument()
    const link = screen.getByRole('link', { name: 'Shemer Kuznits' })
    expect(link).toHaveAttribute('href', '/media/505711')
    expect(screen.getByText(/People/)).toBeInTheDocument()
    expect(screen.queryByText(/no match found/i)).not.toBeInTheDocument()
  })

  it('still says "no match found" when nothing else holds the id', () => {
    renderBox({ enrichmentStatus: 'NotFound' })

    expect(screen.getByText(/no match found/i)).toBeInTheDocument()
    expect(screen.queryByText(/already in chronicle/i)).not.toBeInTheDocument()
  })

  it('a Fix Match the server refuses as a duplicate keeps the panel open and names the existing item', async () => {
    vi.mocked(mediaApi.refreshMediaForPlugin).mockResolvedValue(afterFixMatch(true))
    renderBox()
    const user = userEvent.setup()

    await user.click(screen.getByRole('button', { name: /fix match/i }))
    await user.type(screen.getByPlaceholderText(/open library id or url/i), 'https://openlibrary.org/authors/OL7630410A/Shemer_Kuznits')
    await user.click(screen.getByRole('button', { name: /^apply$/i }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(/already in chronicle as/i)
    expect(alert).toHaveTextContent(/shemer kuznits/i)
    expect(alert).toHaveTextContent(/not added to this item/i)
    expect(screen.getByPlaceholderText(/open library id or url/i)).toBeInTheDocument()        // panel still open
  })

  it('a Fix Match that works closes the panel as before', async () => {
    vi.mocked(mediaApi.refreshMediaForPlugin).mockResolvedValue(afterFixMatch(false))
    renderBox()
    const user = userEvent.setup()

    await user.click(screen.getByRole('button', { name: /fix match/i }))
    await user.type(screen.getByPlaceholderText(/open library id or url/i), 'OL1A')
    await user.click(screen.getByRole('button', { name: /^apply$/i }))

    await waitFor(() => expect(screen.queryByPlaceholderText(/open library id or url/i)).not.toBeInTheDocument())
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('typing a different id clears the refusal message', async () => {
    vi.mocked(mediaApi.refreshMediaForPlugin).mockResolvedValue(afterFixMatch(true))
    renderBox()
    const user = userEvent.setup()
    await user.click(screen.getByRole('button', { name: /fix match/i }))
    await user.type(screen.getByPlaceholderText(/open library id or url/i), 'OL7630410A')
    await user.click(screen.getByRole('button', { name: /^apply$/i }))
    await screen.findByRole('alert')

    await user.type(screen.getByPlaceholderText(/open library id or url/i), 'x')

    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('PluginMetadataBox: link to the page the plugin resolved to', () => {
  const rules = [{ match: '^person:(\\d+)', url: 'https://www.themoviedb.org/person/$1' }]

  it('opens the source page in a new tab, built from the stored id by the manifest rule', () => {
    renderBox({ pluginName: 'TMDB', metadata: { externalId: 'person:1796481' }, sourceUrls: rules, enrichmentStatus: 'Completed' })

    const link = screen.getByRole('link', { name: /open on tmdb/i })
    expect(link).toHaveAttribute('href', 'https://www.themoviedb.org/person/1796481')
    expect(link).toHaveAttribute('target', '_blank')
    expect(link).toHaveAttribute('rel', expect.stringContaining('noopener'))
  })

  it('prefers the address the provider itself gave', () => {
    renderBox({ pluginName: 'Hardcover', metadata: { externalId: 'hardcover:1', sourceUrl: 'https://hardcover.app/books/biomancer-2' }, enrichmentStatus: 'Completed' })

    expect(screen.getByRole('link', { name: /open on hardcover/i })).toHaveAttribute('href', 'https://hardcover.app/books/biomancer-2')
  })

  it('has no link when there is no id, no rule match, or the provider address is not http(s)', () => {
    renderBox({ pluginName: 'TMDB', metadata: { externalId: 'tv:5' }, sourceUrls: rules })
    renderBox({ pluginName: 'Other', metadata: { externalId: 'x', sourceUrl: 'javascript:alert(1)' } })

    expect(screen.queryByRole('link', { name: /open on/i })).not.toBeInTheDocument()
  })
})
