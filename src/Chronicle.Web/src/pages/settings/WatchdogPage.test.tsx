import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import WatchdogPage from './WatchdogPage'
import * as api from '@/api/watchdog'

vi.mock('@/api/watchdog')

const OFF: api.WatchdogStatus = {
  enabled: false, supported: true, unsupportedReason: null, running: false, state: 'off',
  message: 'The watchdog is switched off.', lastCheckUtc: null, lastRestartUtc: null, events: [],
}
const ON: api.WatchdogStatus = {
  ...OFF, enabled: true, running: true, state: 'healthy', message: 'Chronicle is running normally.',
  lastCheckUtc: '2026-10-29T20:00:00Z',
  events: [{ timeUtc: '2026-10-29T19:00:00Z', state: 'restarting', message: 'Chronicle was not working; restarting it.' }],
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getWatchdog).mockResolvedValue(OFF)
})

describe('WatchdogPage', () => {
  it('shows the switch off with no activity list', async () => {
    render(<WatchdogPage />)

    expect(await screen.findByLabelText(/Restart Chronicle automatically/)).not.toBeChecked()
    expect(screen.queryByText('Recent activity')).not.toBeInTheDocument()
  })

  it('switches it on and shows the status and recent activity', async () => {
    vi.mocked(api.setWatchdogEnabled).mockResolvedValue(ON)
    render(<WatchdogPage />)

    await userEvent.click(await screen.findByLabelText(/Restart Chronicle automatically/))

    await waitFor(() => expect(api.setWatchdogEnabled).toHaveBeenCalledWith(true))
    expect(await screen.findByText('Chronicle is running normally.')).toBeInTheDocument()
    expect(screen.getByText(/Chronicle was not working; restarting it\./)).toBeInTheDocument()
  })

  it('explains why it cannot be switched on when this install cannot run it', async () => {
    vi.mocked(api.getWatchdog).mockResolvedValue({
      ...OFF, supported: false, unsupportedReason: 'Chronicle is running as a Windows service, which already restarts itself if it stops.',
    })
    render(<WatchdogPage />)

    expect(await screen.findByText(/already restarts itself/)).toBeInTheDocument()
    expect(screen.getByLabelText(/Restart Chronicle automatically/)).toBeDisabled()
  })

  it('shows an error when switching fails', async () => {
    vi.mocked(api.setWatchdogEnabled).mockRejectedValue(new Error('Boom'))
    render(<WatchdogPage />)

    await userEvent.click(await screen.findByLabelText(/Restart Chronicle automatically/))

    expect(await screen.findByRole('alert')).toHaveTextContent('Boom')
  })
})
