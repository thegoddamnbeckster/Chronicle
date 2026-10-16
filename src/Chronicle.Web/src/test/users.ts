import type { User } from '@/types'

/** A signed-in person for tests that need a definite role. */
export function makeUser(isAdmin: boolean, over: Partial<User> = {}): User {
  return {
    id: 7, username: isAdmin ? 'admin' : 'plain', email: null, displayName: null, isAdmin,
    showDiagnostics: false, showNowPlayingBanner: true, showAllCredits: false, ...over,
  }
}
