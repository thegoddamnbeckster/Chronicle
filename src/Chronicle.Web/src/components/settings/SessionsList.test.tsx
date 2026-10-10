import { describe, it, expect, vi } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import SessionsList from './SessionsList'
import type { SessionInfo } from '@/types'

const CHROME_WIN = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36'

const mine: SessionInfo = {
  id: 'aaaa', createdAt: '2026-10-07T10:00:00Z', lastSeenAt: '2026-10-07T11:00:00Z',
  expiresAt: '2026-11-06T10:00:00Z', userAgent: CHROME_WIN, remoteIp: '10.0.0.5', isCurrent: true,
}
const other: SessionInfo = { ...mine, id: 'bbbb', userAgent: 'Mozilla/5.0 (iPhone) Safari/604', remoteIp: '10.0.0.9', isCurrent: false }

describe('SessionsList', () => {
  it('loads once and labels the current session', async () => {
    const load = vi.fn().mockResolvedValue([mine, other])

    render(<SessionsList load={load} onEnd={vi.fn()} />)

    expect(await screen.findByText(/Chrome on Windows \(this session\)/)).toBeInTheDocument()
    expect(screen.getByText(/Safari on iOS/)).toBeInTheDocument()
    // A stable `load` must not cause a reload loop.
    await new Promise(r => setTimeout(r, 50))
    expect(load).toHaveBeenCalledTimes(1)
  })

  it('offers "End session" only for sessions other than the current one', async () => {
    render(<SessionsList load={vi.fn().mockResolvedValue([mine, other])} onEnd={vi.fn()} />)

    await screen.findByText(/this session/)

    expect(screen.getAllByRole('button', { name: 'End session' })).toHaveLength(1)
  })

  it('ends a session and reloads the list', async () => {
    const load = vi.fn()
      .mockResolvedValueOnce([mine, other])
      .mockResolvedValueOnce([mine])
    const onEnd = vi.fn().mockResolvedValue(undefined)
    render(<SessionsList load={load} onEnd={onEnd} />)

    await userEvent.click(await screen.findByRole('button', { name: 'End session' }))

    expect(onEnd).toHaveBeenCalledWith('bbbb')
    await waitFor(() => expect(screen.queryByRole('button', { name: 'End session' })).not.toBeInTheDocument())
  })

  it('asks for confirmation before ending everything, and does nothing if declined', async () => {
    const run = vi.fn().mockResolvedValue(undefined)
    vi.spyOn(window, 'confirm').mockReturnValue(false)
    render(<SessionsList load={vi.fn().mockResolvedValue([mine])}
                         onEndAll={{ label: 'Sign out everywhere', confirm: 'Sure?', run }} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Sign out everywhere' }))

    expect(window.confirm).toHaveBeenCalledWith('Sure?')
    expect(run).not.toHaveBeenCalled()
  })

  it('runs "end all" once confirmed', async () => {
    const run = vi.fn().mockResolvedValue(undefined)
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    render(<SessionsList load={vi.fn().mockResolvedValue([mine])}
                         onEndAll={{ label: 'Sign out everywhere', confirm: 'Sure?', run }} />)

    await userEvent.click(await screen.findByRole('button', { name: 'Sign out everywhere' }))

    expect(run).toHaveBeenCalledTimes(1)
  })

  it('shows the empty text when there are no sessions', async () => {
    render(<SessionsList load={vi.fn().mockResolvedValue([])} emptyText="Not signed in anywhere." />)

    expect(await screen.findByText('Not signed in anywhere.')).toBeInTheDocument()
  })

  it('shows an error rather than crashing when loading fails', async () => {
    render(<SessionsList load={vi.fn().mockRejectedValue(new Error('boom'))} />)

    expect(await screen.findByText('Could not load sessions.')).toBeInTheDocument()
  })
})
