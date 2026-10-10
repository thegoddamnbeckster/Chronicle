import { createContext, useContext, useState, useEffect, useCallback, type ReactNode } from 'react'
import type { User } from '@/types'
import { getMe, logoutRequest } from '@/api/auth'

interface AuthContextValue {
  user: User | null
  loading: boolean
  logout: () => void
  setUser: (u: User | null) => void
}

export const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    const token = localStorage.getItem('chronicle_token')
    if (!token) {
      setLoading(false)
      return
    }
    getMe()
      .then(setUser)
      .catch(() => localStorage.removeItem('chronicle_token'))
      .finally(() => setLoading(false))
  }, [])

  const logout = useCallback(() => {
    // Tell the server first so the key is dead everywhere, not just forgotten here. Failure
    // (server down, key already invalid) must not trap the user signed-in-looking: clear
    // local state and leave either way.
    void logoutRequest()
      .catch(() => { /* best-effort */ })
      .finally(() => {
        localStorage.removeItem('chronicle_token')
        setUser(null)
        window.location.href = '/login'
      })
  }, [])

  return (
    <AuthContext.Provider value={{ user, loading, logout, setUser }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuthContext(): AuthContextValue {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
