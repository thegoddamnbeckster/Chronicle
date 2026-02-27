import { useState, useEffect, useCallback } from 'react'
import type { User } from '@/types'
import { getMe } from '@/api/auth'

export function useAuth() {
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
      .catch(() => {
        localStorage.removeItem('chronicle_token')
      })
      .finally(() => setLoading(false))
  }, [])

  const logout = useCallback(() => {
    localStorage.removeItem('chronicle_token')
    setUser(null)
    window.location.href = '/login'
  }, [])

  return { user, loading, logout, setUser }
}
