import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import EmailSettingsPage from './EmailSettingsPage'
import * as api from '@/api/emailSettings'

vi.mock('@/api/emailSettings')

const OFF: api.EmailSettings = {
  host: '', port: 587, security: 'starttls', username: null, fromAddress: '', fromName: 'Chronicle',
  publicUrl: null, hasPassword: false, isConfigured: false,
}
const ON: api.EmailSettings = {
  host: 'smtp.example.com', port: 465, security: 'tls', username: 'mailer', fromAddress: 'chronicle@example.com',
  fromName: 'Chronicle', publicUrl: 'https://chronicle.example.com', hasPassword: true, isConfigured: true,
}

beforeEach(() => vi.mocked(api.getEmailSettings).mockResolvedValue(OFF))

describe('EmailSettingsPage', () => {
  it('starts off, and explains what the public address is for', async () => {
    render(<EmailSettingsPage />)

    expect(await screen.findByText('off')).toBeInTheDocument()
    expect(screen.getByText(/nobody can make Chronicle email/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Send test' })).toBeDisabled()
  })

  it('saves what was typed', async () => {
    vi.mocked(api.saveEmailSettings).mockResolvedValue(ON)
    render(<EmailSettingsPage />)
    await screen.findByText('off')

    await userEvent.type(screen.getByLabelText('Mail server'), 'smtp.example.com')
    await userEvent.clear(screen.getByLabelText('Port'))
    await userEvent.type(screen.getByLabelText('Port'), '465')
    await userEvent.selectOptions(screen.getByLabelText('Security'), 'tls')
    await userEvent.type(screen.getByLabelText(/User name/), 'mailer')
    await userEvent.type(screen.getByLabelText('Password'), 'pw')
    await userEvent.type(screen.getByLabelText('Send from (address)'), 'chronicle@example.com')
    await userEvent.type(screen.getByLabelText(/public address/), 'https://chronicle.example.com')
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(api.saveEmailSettings).toHaveBeenCalledWith({
      host: 'smtp.example.com', port: 465, security: 'tls', username: 'mailer', password: 'pw',
      fromAddress: 'chronicle@example.com', fromName: 'Chronicle', publicUrl: 'https://chronicle.example.com',
    }))
    expect(await screen.findByText(/Password-reset emails are on/)).toBeInTheDocument()
  })

  it('keeps the saved password when the box is left blank, and can remove it on request', async () => {
    vi.mocked(api.getEmailSettings).mockResolvedValue(ON)
    vi.mocked(api.saveEmailSettings).mockResolvedValue(ON)
    render(<EmailSettingsPage />)
    const password = await screen.findByLabelText('Password')
    expect(password).toHaveAttribute('placeholder', expect.stringContaining('leave blank to keep'))

    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(api.saveEmailSettings).toHaveBeenLastCalledWith(expect.objectContaining({ password: undefined })))

    await userEvent.click(screen.getByLabelText('Remove the saved password'))
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(api.saveEmailSettings).toHaveBeenLastCalledWith(expect.objectContaining({ password: '' })))
  })

  it('never puts the saved password on the page', async () => {
    vi.mocked(api.getEmailSettings).mockResolvedValue(ON)
    render(<EmailSettingsPage />)

    expect(await screen.findByLabelText('Password')).toHaveValue('')
  })

  it('shows the server\'s reason when saving is refused', async () => {
    vi.mocked(api.saveEmailSettings).mockRejectedValue(new Error('The port must be between 1 and 65535.'))
    render(<EmailSettingsPage />)
    await screen.findByText('off')

    await userEvent.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The port must be between 1 and 65535.')
  })

  it('sends a test message, and shows exactly what went wrong if it fails', async () => {
    vi.mocked(api.getEmailSettings).mockResolvedValue(ON)
    vi.mocked(api.sendTestEmail).mockResolvedValueOnce().mockRejectedValueOnce(new Error('The mail server rejected the user name or password.'))
    render(<EmailSettingsPage />)
    await userEvent.type(await screen.findByLabelText('To'), 'me@example.com')

    await userEvent.click(screen.getByRole('button', { name: 'Send test' }))
    expect(await screen.findByText(/test message was sent to me@example.com/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Send test' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('rejected the user name or password')
  })
})
