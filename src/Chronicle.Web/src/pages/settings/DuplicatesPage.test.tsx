import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import DuplicatesPage from './DuplicatesPage'
import { renderWithProviders } from '@/test/test-utils'
import * as duplicatesApi from '@/api/duplicates'
import * as mediaApi from '@/api/media'
import type { DuplicateCandidate, DuplicateCandidateItem } from '@/api/duplicates'

vi.mock('@/api/duplicates')
vi.mock('@/api/media')

const mockedGetDuplicateCandidates = vi.mocked(duplicatesApi.getDuplicateCandidates)
const mockedDismissDuplicate = vi.mocked(duplicatesApi.dismissDuplicate)
const mockedDeleteMedia = vi.mocked(mediaApi.deleteMedia)

function makeItem(overrides: Partial<DuplicateCandidateItem> & { id: number; name: string }): DuplicateCandidateItem {
  return {
    posterUrl: null,
    hierarchyLevel: 0,
    year: null,
    overview: null,
    mediaType: 'tv',
    externalIds: [],
    filePath: null,
    ...overrides,
  }
}

function makeCandidate(candidateId: number, itemA: DuplicateCandidateItem, itemB: DuplicateCandidateItem): DuplicateCandidate {
  return { candidateId, itemA, itemB }
}

beforeEach(() => {
  vi.clearAllMocks()
  vi.spyOn(window, 'confirm').mockReturnValue(true)
  mockedDismissDuplicate.mockResolvedValue(undefined)
  mockedDeleteMedia.mockResolvedValue(undefined)
})

/** Root-caused live (2026-09-19): the "Delete this one" button ships with a destructive
 * DELETE call gated only by window.confirm -- these pin that the confirm gate is actually
 * respected (declining does nothing) and that the right item id is deleted, since a future
 * refactor of the mutation/confirm flow has nothing else to catch a regression here. */
describe('DuplicatesPage', () => {
  it('deletes the confirmed item and leaves the other one alone', async () => {
    const user = userEvent.setup()
    const itemA = makeItem({ id: 101, name: 'Real Episode' })
    const itemB = makeItem({ id: 102, name: 'Phantom Episode' })
    mockedGetDuplicateCandidates.mockResolvedValue({
      data: [makeCandidate(1, itemA, itemB)],
      pagination: { page: 1, perPage: 20, total: 1 },
    })

    renderWithProviders(<DuplicatesPage />)
    await screen.findByText('Real Episode')

    const deleteButtons = await screen.findAllByRole('button', { name: 'Delete this one' })
    await user.click(deleteButtons[1]) // "Phantom Episode" side

    await waitFor(() => expect(mockedDeleteMedia).toHaveBeenCalledWith(102))
    expect(mockedDeleteMedia).not.toHaveBeenCalledWith(101)
  })

  it('does not delete anything when the confirm dialog is declined', async () => {
    const user = userEvent.setup()
    vi.mocked(window.confirm).mockReturnValueOnce(false)
    const itemA = makeItem({ id: 101, name: 'Real Episode' })
    const itemB = makeItem({ id: 102, name: 'Phantom Episode' })
    mockedGetDuplicateCandidates.mockResolvedValue({
      data: [makeCandidate(1, itemA, itemB)],
      pagination: { page: 1, perPage: 20, total: 1 },
    })

    renderWithProviders(<DuplicatesPage />)
    await screen.findByText('Real Episode')

    const deleteButtons = await screen.findAllByRole('button', { name: 'Delete this one' })
    await user.click(deleteButtons[0])

    expect(mockedDeleteMedia).not.toHaveBeenCalled()
  })

  /** Pins the fix for a real race: a single shared mutation's own isPending/variables only
   * ever reflected the MOST RECENT delete call, so deleting item X then item Y (before X
   * resolved) made X's button look idle again while X's DELETE was still in flight. Each
   * button's own disabled state must track its own item, not whichever delete fired last. */
  it('tracks each delete button\'s own in-flight state independently', async () => {
    const user = userEvent.setup()
    let resolveFirstDelete: () => void = () => {}
    mockedDeleteMedia.mockImplementation((id: number) => {
      if (id === 101) return new Promise(resolve => { resolveFirstDelete = () => resolve(undefined) })
      return Promise.resolve()
    })
    const itemA = makeItem({ id: 101, name: 'Item A' })
    const itemB = makeItem({ id: 102, name: 'Item B' })
    mockedGetDuplicateCandidates.mockResolvedValue({
      data: [makeCandidate(1, itemA, itemB)],
      pagination: { page: 1, perPage: 20, total: 1 },
    })

    renderWithProviders(<DuplicatesPage />)
    await screen.findByText('Item A')

    const [deleteA, deleteB] = await screen.findAllByRole('button', { name: 'Delete this one' })
    await user.click(deleteA) // starts a delete for item A that never resolves yet

    await waitFor(() => expect(screen.getByRole('button', { name: 'Deleting…' })).toBeInTheDocument())
    // Item B's own button must still be idle -- it was never clicked, and the still-pending
    // delete for A must not bleed its "deleting" state onto B's button.
    expect(deleteB).toHaveTextContent('Delete this one')
    expect(deleteB).not.toBeDisabled()

    resolveFirstDelete()
    await waitFor(() => expect(mockedDeleteMedia).toHaveBeenCalledWith(101))
  })
})
