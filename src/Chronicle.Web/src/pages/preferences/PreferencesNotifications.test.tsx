import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import PreferencesPage from './PreferencesPage'
import * as notifications from '@/api/notifications'
import * as users from '@/api/users'
import { useAuth } from '@/hooks/useAuth'
import { useTheme } from '@/contexts/ThemeContext'

vi.mock('@/api/notifications')
vi.mock('@/api/users')
vi.mock('@/hooks/useAuth')
vi.mock('@/contexts/ThemeContext')

beforeEach(() => {
  vi.mocked(useAuth).mockReturnValue({
    user: { id: 1, username: 'a', email: null, displayName: null, isAdmin: true, showDiagnostics: false, showNowPlayingBanner: true, showAllCredits: false },
    loading: false, logout: vi.fn(), setUser: vi.fn(),
  })
  vi.mocked(useTheme).mockReturnValue({ themes: [], activeKey: '', loading: false, setTheme: vi.fn() })
  vi.mocked(notifications.listNotificationKinds).mockResolvedValue([
    { kind: 'task.failed', label: 'A background task failed', description: 'A scheduled task stopped.' },
    { kind: 'plugin.update', label: 'A plugin has an update', description: 'A newer plugin version exists.' },
  ])
  vi.mocked(users.getMyPreferences).mockResolvedValue({ mutedNotificationKinds: ['plugin.update'] })
  vi.mocked(users.updateMyPreferences).mockResolvedValue()
})

describe('PreferencesPage notifications', () => {
  it('lists each kind, ticked unless it was switched off', async () => {
    render(<PreferencesPage />)

    expect(await screen.findByLabelText('A background task failed')).toBeChecked()
    expect(screen.getByLabelText('A plugin has an update')).not.toBeChecked()
    expect(screen.getByText('A newer plugin version exists.')).toBeInTheDocument()
  })

  it('switching one off saves the full list of switched-off kinds', async () => {
    render(<PreferencesPage />)

    await userEvent.click(await screen.findByLabelText('A background task failed'))

    await waitFor(() => expect(users.updateMyPreferences).toHaveBeenCalledWith({ mutedNotificationKinds: ['plugin.update', 'task.failed'] }))
    expect(screen.getByLabelText('A background task failed')).not.toBeChecked()
  })

  it('switching one back on removes it from the list', async () => {
    render(<PreferencesPage />)

    await userEvent.click(await screen.findByLabelText('A plugin has an update'))

    await waitFor(() => expect(users.updateMyPreferences).toHaveBeenCalledWith({ mutedNotificationKinds: [] }))
  })

  it('puts the box back if saving fails', async () => {
    vi.mocked(users.updateMyPreferences).mockRejectedValue(new Error('offline'))
    render(<PreferencesPage />)

    await userEvent.click(await screen.findByLabelText('A background task failed'))

    await waitFor(() => expect(screen.getByLabelText('A background task failed')).toBeChecked())
  })

  it('shows no notifications section when the kinds cannot be loaded', async () => {
    vi.mocked(notifications.listNotificationKinds).mockRejectedValue(new Error('x'))
    render(<PreferencesPage />)

    await screen.findByText('Preferences')
    expect(screen.queryByText('Choose what shows up under the bell.')).not.toBeInTheDocument()
  })
})
