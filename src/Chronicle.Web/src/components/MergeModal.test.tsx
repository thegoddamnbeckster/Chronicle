import { describe, it, expect, vi } from 'vitest'
import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import MergeModal from './MergeModal'
import { renderWithProviders } from '@/test/test-utils'
import * as duplicates from '@/api/duplicates'

vi.mock('@/api/duplicates')

const a = { id: 1, name: 'Some Collection', posterUrl: null }
const b = { id: 2, name: 'Some Collection', posterUrl: null }

describe('MergeModal', () => {
  it('tells the user why a merge was refused instead of a generic failure', async () => {
    vi.mocked(duplicates.mergeItems).mockRejectedValue(new Error('the item to absorb is a collection and the one to keep is not'))
    renderWithProviders(<MergeModal itemA={a} itemB={b} onClose={vi.fn()} onMerged={vi.fn()} />)

    await userEvent.click(screen.getAllByRole('button', { name: /Some Collection/ })[0])
    await userEvent.click(screen.getByRole('button', { name: 'Confirm Merge' }))

    expect(await screen.findByText(/the item to absorb is a collection/)).toBeInTheDocument()
    expect(screen.queryByText('Merge failed. Please try again.')).not.toBeInTheDocument()
  })

  it('still says "Merge failed" when the error carries no message', async () => {
    vi.mocked(duplicates.mergeItems).mockRejectedValue(new Error(''))
    renderWithProviders(<MergeModal itemA={a} itemB={b} onClose={vi.fn()} onMerged={vi.fn()} />)

    await userEvent.click(screen.getAllByRole('button', { name: /Some Collection/ })[0])
    await userEvent.click(screen.getByRole('button', { name: 'Confirm Merge' }))

    expect(await screen.findByText('Merge failed. Please try again.')).toBeInTheDocument()
  })
})
