import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { AuthContext } from '@/contexts/AuthContext'
import { useCanDeleteMedia } from './useCanDeleteMedia'
import * as rights from '@/api/mediaRights'
import type { User } from '@/types'

vi.mock('@/api/mediaRights')

const user = (isAdmin: boolean): User => ({
  id: 1, username: 'u', email: null, displayName: null, isAdmin, showDiagnostics: false, showNowPlayingBanner: true, showAllCredits: false,
})

function wrapperFor(u: User | null) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } })
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={client}>
        <AuthContext.Provider value={{ user: u, loading: false, logout: vi.fn(), setUser: vi.fn() }}>{children}</AuthContext.Provider>
      </QueryClientProvider>
    )
  }
}

beforeEach(() => vi.mocked(rights.getMediaRights).mockReset())

describe('useCanDeleteMedia', () => {
  it('lets an administrator delete without even asking', () => {
    const { result } = renderHook(() => useCanDeleteMedia(), { wrapper: wrapperFor(user(true)) })
    expect(result.current).toBe(true)
    expect(rights.getMediaRights).not.toHaveBeenCalled()
  })

  it('lets everyone delete while deletion is open', async () => {
    vi.mocked(rights.getMediaRights).mockResolvedValue({ deleteAdminOnly: false })
    const { result } = renderHook(() => useCanDeleteMedia(), { wrapper: wrapperFor(user(false)) })
    await waitFor(() => expect(rights.getMediaRights).toHaveBeenCalled())
    expect(result.current).toBe(true)
  })

  it('takes the button away from an ordinary user once deletion is reserved', async () => {
    vi.mocked(rights.getMediaRights).mockResolvedValue({ deleteAdminOnly: true })
    const { result } = renderHook(() => useCanDeleteMedia(), { wrapper: wrapperFor(user(false)) })
    await waitFor(() => expect(result.current).toBe(false))
  })

  it('applies no restriction outside a signed-in session', () => {
    const { result } = renderHook(() => useCanDeleteMedia(), { wrapper: wrapperFor(null) })
    expect(result.current).toBe(true)
  })
})
