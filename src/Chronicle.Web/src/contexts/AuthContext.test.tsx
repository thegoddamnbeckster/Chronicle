import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { AuthProvider, useAuthContext } from './AuthContext'
import * as authApi from '@/api/auth'

vi.mock('@/api/auth')

function LogoutButton() {
  const { logout } = useAuthContext()
  return <button onClick={logout}>logout</button>
}

describe('AuthProvider logout', () => {
  const realLocation = window.location
  let navigations: string[]

  beforeEach(() => {
    localStorage.clear()
    navigations = []
    Object.defineProperty(window, 'location', {
      configurable: true,
      value: { ...realLocation, set href(v: string) { navigations.push(v) }, get href() { return 'http://localhost/' } },
    })
  })
  afterEach(() => Object.defineProperty(window, 'location', { configurable: true, value: realLocation }))

  it('tells the server to end the session, then clears the key and goes to /login', async () => {
    localStorage.setItem('chronicle_token', 'chr_sess_abc')
    vi.mocked(authApi.getMe).mockResolvedValue({
      id: 1, username: 'a', email: null, displayName: null, isAdmin: false,
      showDiagnostics: false, showNowPlayingBanner: true, showAllCredits: false,
    })
    vi.mocked(authApi.logoutRequest).mockResolvedValue()
    render(<AuthProvider><LogoutButton /></AuthProvider>)

    await userEvent.click(await screen.findByText('logout'))

    await waitFor(() => expect(navigations).toContain('/login'))
    expect(authApi.logoutRequest).toHaveBeenCalledTimes(1)
    expect(localStorage.getItem('chronicle_token')).toBeNull()
  })

  it('still signs out locally when the server call fails', async () => {
    localStorage.setItem('chronicle_token', 'chr_sess_abc')
    vi.mocked(authApi.getMe).mockResolvedValue({
      id: 1, username: 'a', email: null, displayName: null, isAdmin: false,
      showDiagnostics: false, showNowPlayingBanner: true, showAllCredits: false,
    })
    vi.mocked(authApi.logoutRequest).mockRejectedValue(new Error('server down'))
    render(<AuthProvider><LogoutButton /></AuthProvider>)

    await userEvent.click(await screen.findByText('logout'))

    await waitFor(() => expect(navigations).toContain('/login'))
    expect(localStorage.getItem('chronicle_token')).toBeNull()
  })
})
