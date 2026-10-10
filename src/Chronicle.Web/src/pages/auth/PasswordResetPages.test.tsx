import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import ForgotPasswordPage from './ForgotPasswordPage'
import ResetPasswordPage from './ResetPasswordPage'
import * as auth from '@/api/auth'

vi.mock('@/api/auth', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/auth')>()
  return { ...actual, forgotPassword: vi.fn(), resetPassword: vi.fn() }
})

function setHash(hash: string) {
  window.history.replaceState(null, '', '/reset-password' + hash)
}

beforeEach(() => setHash(''))
afterEach(() => setHash(''))

describe('ForgotPasswordPage', () => {
  it('asks for a username or email and says the same thing whatever happens', async () => {
    vi.mocked(auth.forgotPassword).mockResolvedValue({ emailEnabled: true })
    render(<MemoryRouter><ForgotPasswordPage /></MemoryRouter>)

    await userEvent.type(screen.getByLabelText('Username or email address'), '  alice  ')
    await userEvent.click(screen.getByRole('button', { name: 'Send reset link' }))

    expect(await screen.findByText(/a reset link is on its way/)).toBeInTheDocument()
    expect(auth.forgotPassword).toHaveBeenCalledWith('alice')
    expect(screen.queryByRole('status')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'I have a reset code' })).toHaveAttribute('href', '/reset-password')
  })

  it('tells people when this server cannot send email, and points to an administrator', async () => {
    vi.mocked(auth.forgotPassword).mockResolvedValue({ emailEnabled: false })
    render(<MemoryRouter><ForgotPasswordPage /></MemoryRouter>)

    await userEvent.type(screen.getByLabelText('Username or email address'), 'alice')
    await userEvent.click(screen.getByRole('button', { name: 'Send reset link' }))

    expect(await screen.findByRole('status')).toHaveTextContent(/not set up to send email.*administrator/)
  })

  it('shows the server\'s reason when the request is refused (for example, too many requests)', async () => {
    vi.mocked(auth.forgotPassword).mockRejectedValue(new Error('Too many password reset requests. Try again in 40 minutes.'))
    render(<MemoryRouter><ForgotPasswordPage /></MemoryRouter>)

    await userEvent.type(screen.getByLabelText('Username or email address'), 'alice')
    await userEvent.click(screen.getByRole('button', { name: 'Send reset link' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Too many password reset requests')
  })

  it('will not send an empty request', () => {
    render(<MemoryRouter><ForgotPasswordPage /></MemoryRouter>)

    expect(screen.getByRole('button', { name: 'Send reset link' })).toBeDisabled()
  })
})

describe('ResetPasswordPage', () => {
  it('reads the code from the link, then removes it from the address bar', async () => {
    setHash('#token=abc_DEF-123')
    render(<MemoryRouter><ResetPasswordPage /></MemoryRouter>)

    expect(screen.getByLabelText('Reset code')).toHaveValue('abc_DEF-123')
    await waitFor(() => expect(window.location.hash).toBe(''))
  })

  it('lets someone type a code an administrator gave them', async () => {
    vi.mocked(auth.resetPassword).mockResolvedValue()
    render(<MemoryRouter><ResetPasswordPage /></MemoryRouter>)

    await userEvent.type(screen.getByLabelText('Reset code'), '  handed-over-code ')
    await userEvent.type(screen.getByLabelText('New password'), 'a-good-password')
    await userEvent.type(screen.getByLabelText('Confirm new password'), 'a-good-password')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))

    await waitFor(() => expect(auth.resetPassword).toHaveBeenCalledWith('handed-over-code', 'a-good-password'))
    expect(await screen.findByText('Password changed')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/login')
  })

  it('checks length and matching before bothering the server', async () => {
    setHash('#token=abc')
    render(<MemoryRouter><ResetPasswordPage /></MemoryRouter>)

    await userEvent.type(screen.getByLabelText('New password'), 'short')
    await userEvent.type(screen.getByLabelText('Confirm new password'), 'short')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('at least 8 characters')

    await userEvent.clear(screen.getByLabelText('New password'))
    await userEvent.clear(screen.getByLabelText('Confirm new password'))
    await userEvent.type(screen.getByLabelText('New password'), 'long-enough-1')
    await userEvent.type(screen.getByLabelText('Confirm new password'), 'long-enough-2')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('do not match')

    expect(auth.resetPassword).not.toHaveBeenCalled()
  })

  it('shows the server\'s explanation for a bad or used-up code, and offers a new link', async () => {
    setHash('#token=stale')
    vi.mocked(auth.resetPassword).mockRejectedValue(new Error('That reset code is not valid. It may have expired or already been used - ask for a new one.'))
    render(<MemoryRouter><ResetPasswordPage /></MemoryRouter>)

    await userEvent.type(screen.getByLabelText('New password'), 'long-enough-1')
    await userEvent.type(screen.getByLabelText('Confirm new password'), 'long-enough-1')
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('may have expired or already been used')
    expect(screen.getByRole('link', { name: 'Ask for a new link' })).toHaveAttribute('href', '/forgot-password')
    expect(screen.queryByText('Password changed')).not.toBeInTheDocument()
  })

  it('needs a code before it will submit', () => {
    render(<MemoryRouter><ResetPasswordPage /></MemoryRouter>)

    expect(screen.getByRole('button', { name: 'Change password' })).toBeDisabled()
  })
})
