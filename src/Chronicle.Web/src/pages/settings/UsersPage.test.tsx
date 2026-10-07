import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import UsersPage from './UsersPage'
import * as users from '@/api/users'
import { useAuth } from '@/hooks/useAuth'

vi.mock('@/api/users', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/users')>()
  return { ...actual, listUsers: vi.fn(), issueResetToken: vi.fn() }
})
vi.mock('@/hooks/useAuth')

const account = (over: Partial<users.UserAccountDto>): users.UserAccountDto => ({
  id: 1, username: 'admin', email: null, firstName: null, lastName: null, handle: null, displayName: null,
  resolvedDisplayName: 'admin', isAdmin: true, isActive: true, createdAt: '2026-01-01T00:00:00Z', lastLoginAt: null, contacts: [], ...over,
})

beforeEach(() => {
  vi.mocked(useAuth).mockReturnValue({
    user: { id: 1, username: 'admin', email: null, displayName: null, isAdmin: true, showDiagnostics: false, showNowPlayingBanner: true, showAllCredits: false },
    loading: false, logout: vi.fn(), setUser: vi.fn(),
  })
  vi.mocked(users.listUsers).mockResolvedValue([
    account({ id: 1 }),
    account({ id: 2, username: 'bob', resolvedDisplayName: 'bob', isAdmin: false }),
    account({ id: 3, username: 'gone', resolvedDisplayName: 'gone', isAdmin: false, isActive: false }),
  ])
})

describe('UsersPage reset codes', () => {
  async function rowFor(name: string) {
    const label = await screen.findByText(name, { selector: 'span' })
    return label.closest('div')!.parentElement as HTMLElement
  }

  it('shows a one-time code for the chosen person, with the link, and can be dismissed', async () => {
    vi.mocked(users.issueResetToken).mockResolvedValue({
      token: 'TOKEN-123', expiresAt: '2030-01-01T00:00:00Z', resetUrl: 'https://c.example.com/reset-password#token=TOKEN-123', username: 'bob',
    })
    render(<UsersPage />)

    await userEvent.click(within(await rowFor('bob')).getByRole('button', { name: 'Reset Code' }))

    const panel = await screen.findByRole('dialog', { name: 'Reset code' })
    expect(users.issueResetToken).toHaveBeenCalledWith(2)
    expect(within(panel).getByTestId('reset-code')).toHaveTextContent('TOKEN-123')
    expect(panel).toHaveTextContent('It will not be shown again')
    expect(panel).toHaveTextContent('https://c.example.com/reset-password#token=TOKEN-123')

    await userEvent.click(within(panel).getByRole('button', { name: 'Done' }))
    expect(screen.queryByRole('dialog', { name: 'Reset code' })).not.toBeInTheDocument()
    expect(screen.queryByText('TOKEN-123')).not.toBeInTheDocument()
  })

  it('is not offered for a deactivated account', async () => {
    render(<UsersPage />)

    expect(within(await rowFor('gone')).getByRole('button', { name: 'Reset Code' })).toBeDisabled()
  })

  it('says why when the server refuses', async () => {
    vi.mocked(users.issueResetToken).mockRejectedValue({ response: { data: { error: { message: 'That account is deactivated; reactivate it before issuing a reset.' } } } })
    render(<UsersPage />)

    await userEvent.click(within(await rowFor('bob')).getByRole('button', { name: 'Reset Code' }))

    expect(await screen.findByText(/reactivate it before issuing a reset/)).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Reset code' })).not.toBeInTheDocument()
  })
})
