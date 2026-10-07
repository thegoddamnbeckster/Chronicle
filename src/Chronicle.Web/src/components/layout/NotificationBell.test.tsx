import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import NotificationBell from './NotificationBell'
import { UNREAD_KEY } from '@/hooks/useLiveUpdates'
import * as api from '@/api/notifications'

vi.mock('@/api/notifications')

const note = (over: Partial<api.NotificationItem>): api.NotificationItem => ({
  id: 1, kind: 'task.failed', title: 'Database Backup failed', body: 'Disk is full.', link: '/settings/background-tasks',
  createdAt: new Date(Date.now() - 5 * 60_000).toISOString(), isRead: false, ...over,
})

function Where() { return <span data-testid="where">{useLocation().pathname}</span> }

function renderBell(unread: number) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  qc.setQueryData(UNREAD_KEY, unread)
  render(
    <QueryClientProvider client={qc}>
      <MemoryRouter initialEntries={['/']}>
        <NotificationBell />
        <Routes><Route path="*" element={<Where />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return qc
}

beforeEach(() => {
  vi.mocked(api.listNotifications).mockResolvedValue({ unread: 2, items: [note({}), note({ id: 2, title: 'Old news', isRead: true, link: null, body: null })] })
  vi.mocked(api.markNotificationRead).mockResolvedValue()
  vi.mocked(api.markAllNotificationsRead).mockResolvedValue()
  vi.mocked(api.deleteNotification).mockResolvedValue()
  vi.mocked(api.clearReadNotifications).mockResolvedValue()
})

describe('NotificationBell', () => {
  it('shows nothing on the bell when there is nothing unread', () => {
    renderBell(0)

    expect(screen.getByRole('button', { name: 'Notifications' })).toBeInTheDocument()
    expect(screen.queryByTestId('unread-badge')).not.toBeInTheDocument()
  })

  it('shows the unread count, capped at 99+', () => {
    renderBell(3)
    expect(screen.getByRole('button', { name: 'Notifications, 3 unread' })).toBeInTheDocument()
    expect(screen.getByTestId('unread-badge')).toHaveTextContent('3')
  })

  it('caps a huge count', () => {
    renderBell(250)
    expect(screen.getByTestId('unread-badge')).toHaveTextContent('99+')
  })

  it('opens a panel with the notices, newest first as the server sent them, and how long ago', async () => {
    renderBell(1)

    await userEvent.click(screen.getByRole('button', { name: /Notifications/ }))

    const panel = await screen.findByRole('dialog', { name: 'Notifications' })
    expect(await within(panel).findByText('Database Backup failed')).toBeInTheDocument()
    expect(within(panel).getByText('Disk is full.')).toBeInTheDocument()
    expect(within(panel).getAllByText('5 min ago')).toHaveLength(2)
    expect(within(panel).getByText('Old news')).toBeInTheDocument()
  })

  it('does not fetch the list until the panel is opened', () => {
    renderBell(1)

    expect(api.listNotifications).not.toHaveBeenCalled()
  })

  it('clicking a notice marks it read, lowers the count and goes to its page', async () => {
    const qc = renderBell(1)
    await userEvent.click(screen.getByRole('button', { name: /Notifications/ }))

    await userEvent.click(await screen.findByText('Database Backup failed'))

    await waitFor(() => expect(api.markNotificationRead).toHaveBeenCalledWith(1))
    expect(qc.getQueryData(UNREAD_KEY)).toBe(0)
    expect(screen.getByTestId('where')).toHaveTextContent('/settings/background-tasks')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('clicking an already-read notice without a link just closes the panel', async () => {
    renderBell(1)
    await userEvent.click(screen.getByRole('button', { name: /Notifications/ }))

    await userEvent.click(await screen.findByText('Old news'))

    expect(api.markNotificationRead).not.toHaveBeenCalled()
    expect(screen.getByTestId('where')).toHaveTextContent('/')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('marks everything read', async () => {
    const qc = renderBell(2)
    await userEvent.click(screen.getByRole('button', { name: /Notifications/ }))

    await userEvent.click(await screen.findByRole('button', { name: 'Mark all read' }))

    await waitFor(() => expect(api.markAllNotificationsRead).toHaveBeenCalled())
    expect(qc.getQueryData(UNREAD_KEY)).toBe(0)
  })

  it('disables "Mark all read" when there is nothing unread', async () => {
    renderBell(0)
    await userEvent.click(screen.getByRole('button', { name: 'Notifications' }))

    expect(await screen.findByRole('button', { name: 'Mark all read' })).toBeDisabled()
  })

  it('dismisses one notice, lowering the count only if it was unread', async () => {
    const qc = renderBell(1)
    await userEvent.click(screen.getByRole('button', { name: /Notifications/ }))

    await userEvent.click(await screen.findByRole('button', { name: 'Dismiss Old news' }))
    await waitFor(() => expect(api.deleteNotification).toHaveBeenCalledWith(2))
    expect(qc.getQueryData(UNREAD_KEY)).toBe(1)

    await userEvent.click(screen.getByRole('button', { name: 'Dismiss Database Backup failed' }))
    await waitFor(() => expect(api.deleteNotification).toHaveBeenCalledWith(1))
    expect(qc.getQueryData(UNREAD_KEY)).toBe(0)
  })

  it('clears what has been read', async () => {
    renderBell(1)
    await userEvent.click(screen.getByRole('button', { name: /Notifications/ }))

    await userEvent.click(await screen.findByRole('button', { name: 'Clear read' }))

    await waitFor(() => expect(api.clearReadNotifications).toHaveBeenCalled())
  })

  it('says so when there is nothing', async () => {
    vi.mocked(api.listNotifications).mockResolvedValue({ unread: 0, items: [] })
    renderBell(0)
    await userEvent.click(screen.getByRole('button', { name: 'Notifications' }))

    expect(await screen.findByText('Nothing new.')).toBeInTheDocument()
  })

  it('closes on Escape and on a click elsewhere', async () => {
    renderBell(1)
    await userEvent.click(screen.getByRole('button', { name: /Notifications/ }))
    await screen.findByRole('dialog')

    await userEvent.keyboard('{Escape}')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /Notifications/ }))
    await screen.findByRole('dialog')
    await userEvent.click(document.body)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('offers a way to choose what you are told about', async () => {
    renderBell(1)
    await userEvent.click(screen.getByRole('button', { name: /Notifications/ }))

    await userEvent.click(await screen.findByRole('link', { name: 'Choose what you are told about' }))

    expect(screen.getByTestId('where')).toHaveTextContent('/preferences')
  })
})
