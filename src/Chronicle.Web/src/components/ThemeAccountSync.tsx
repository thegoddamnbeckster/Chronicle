import { useEffect, useRef, useState } from 'react'
import { useAuth } from '@/hooks/useAuth'
import { useTheme } from '@/contexts/ThemeContext'
import { getMyPreferences, updateMyPreferences } from '@/api/users'

/**
 * Bridges ThemeContext (localStorage-only, no knowledge of the signed-in user) with
 * the account's server-side preferences, so a theme choice follows the user across
 * devices instead of being stuck per-browser.
 *
 * Rendered inside AuthProvider's subtree specifically because ThemeProvider sits
 * *above* AuthProvider in main.tsx (theme needs to be ready before auth resolves,
 * for zero-flash rendering) -- so ThemeContext itself has no way to reach useAuth().
 * This component is the one place with access to both.
 */
export function ThemeAccountSync() {
  const { user } = useAuth()
  const { activeKey, setTheme, themes, loading: themesLoading } = useTheme()

  const pulledForUser = useRef<number | null>(null)
  const [hasPulled, setHasPulled] = useState(false)

  // Pull once per login: apply the account's saved theme, if it has one.
  useEffect(() => {
    if (!user) {
      pulledForUser.current = null
      setHasPulled(false)
      return
    }
    if (themesLoading || themes.length === 0) return
    if (pulledForUser.current === user.id) return
    pulledForUser.current = user.id

    void (async () => {
      try {
        const prefs = await getMyPreferences()
        if (prefs.theme && prefs.theme !== activeKey) setTheme(prefs.theme)
      } catch {
        // Best-effort — keep whatever theme is already active locally.
      } finally {
        setHasPulled(true)
      }
    })()
  }, [user, themesLoading, themes, activeKey, setTheme])

  // Push subsequent theme changes back to the account. Gated on hasPulled so the
  // very first render after login (before the pull above resolves) can't push
  // whatever was active locally pre-login and clobber the account's saved theme.
  useEffect(() => {
    if (!user || !hasPulled) return
    void updateMyPreferences({ theme: activeKey }).catch(() => {
      // Best-effort — a failed sync just means the next device won't see this
      // change; local theming is unaffected either way.
    })
  }, [activeKey, user, hasPulled])

  return null
}
