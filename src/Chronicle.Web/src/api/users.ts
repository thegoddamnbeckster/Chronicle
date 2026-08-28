import client from './client'

export interface UserPreferences {
  showDiagnostics?: boolean
  defaultFoldsOpen?: boolean
  folds?: Record<string, boolean>
  createCollectionStubs?: boolean
  /** When true (default), the "Now Playing" banner shows active playback sessions at the
   *  top of the main content area. When false, it never renders. */
  showNowPlayingBanner?: boolean
  /** Active theme storage key ("{pluginId}:{themeKey}"), synced across devices. */
  theme?: string
}

export async function getMyPreferences(): Promise<UserPreferences> {
  const { data } = await client.get<{ success: boolean; data: UserPreferences }>('/users/me/preferences')
  return data.data ?? {}
}

export async function updateMyPreferences(prefs: UserPreferences): Promise<void> {
  await client.patch('/users/me/preferences', prefs)
}

// ── Accounts, profiles, and contacts ──────────────────────────────────────────

export interface UserContactDto {
  id: number
  kind: string
  label: string | null
  value: string
  isPrimary: boolean
  createdAt: string
}

export interface UserAccountDto {
  id: number
  username: string
  email: string | null
  firstName: string | null
  lastName: string | null
  handle: string | null
  displayName: string | null
  /** Server-computed: displayName → handle → first+last → username. */
  resolvedDisplayName: string
  isAdmin: boolean
  isActive: boolean
  createdAt: string
  lastLoginAt: string | null
  contacts: UserContactDto[]
}

/** Full replacement, not a patch — an omitted field clears that value. */
export interface ProfileInput {
  email?: string | null
  firstName?: string | null
  lastName?: string | null
  handle?: string | null
  displayName?: string | null
}

export interface ContactInput {
  kind: string
  label?: string | null
  value: string
  isPrimary: boolean
}

/**
 * Kinds offered in the picker. The backend accepts any string, so this is a convenience
 * only — "Other…" lets a user enter a kind that isn't listed without needing a release.
 */
export const CONTACT_KINDS = [
  'email', 'phone', 'website', 'bluesky', 'discord', 'facebook', 'github',
  'instagram', 'linkedin', 'mastodon', 'matrix', 'reddit', 'signal',
  'telegram', 'threads', 'tiktok', 'twitch', 'x', 'youtube',
] as const

// Self-service

export async function getMyProfile(): Promise<UserAccountDto> {
  const { data } = await client.get<{ data: UserAccountDto }>('/users/me/profile')
  return data.data
}

export async function updateMyProfile(input: ProfileInput): Promise<UserAccountDto> {
  const { data } = await client.put<{ data: UserAccountDto }>('/users/me/profile', input)
  return data.data
}

export async function changeMyPassword(currentPassword: string, newPassword: string): Promise<void> {
  await client.put('/users/me/password', { currentPassword, newPassword })
}

export async function addMyContact(input: ContactInput): Promise<UserContactDto> {
  const { data } = await client.post<{ data: UserContactDto }>('/users/me/contacts', input)
  return data.data
}

export async function updateMyContact(id: number, input: ContactInput): Promise<UserContactDto> {
  const { data } = await client.put<{ data: UserContactDto }>(`/users/me/contacts/${id}`, input)
  return data.data
}

export async function deleteMyContact(id: number): Promise<void> {
  await client.delete(`/users/me/contacts/${id}`)
}

// Administration

export async function listUsers(): Promise<UserAccountDto[]> {
  const { data } = await client.get<{ data: UserAccountDto[] }>('/users')
  return data.data
}

export interface CreateUserInput {
  username: string
  password: string
  email?: string | null
  firstName?: string | null
  lastName?: string | null
  handle?: string | null
  isAdmin: boolean
}

export async function createUser(input: CreateUserInput): Promise<UserAccountDto> {
  const { data } = await client.post<{ data: UserAccountDto }>('/users', input)
  return data.data
}

export async function updateUserProfile(id: number, input: ProfileInput): Promise<UserAccountDto> {
  const { data } = await client.put<{ data: UserAccountDto }>(`/users/${id}/profile`, input)
  return data.data
}

export async function setUserAdmin(id: number, isAdmin: boolean): Promise<UserAccountDto> {
  const { data } = await client.put<{ data: UserAccountDto }>(`/users/${id}/admin`, { isAdmin })
  return data.data
}

export async function setUserActive(id: number, isActive: boolean): Promise<UserAccountDto> {
  const { data } = await client.put<{ data: UserAccountDto }>(`/users/${id}/active`, { isActive })
  return data.data
}

export async function resetUserPassword(id: number, newPassword: string): Promise<void> {
  await client.put(`/users/${id}/password`, { newPassword })
}

export async function deleteUser(id: number): Promise<void> {
  await client.delete(`/users/${id}`)
}

export async function addUserContact(userId: number, input: ContactInput): Promise<UserContactDto> {
  const { data } = await client.post<{ data: UserContactDto }>(`/users/${userId}/contacts`, input)
  return data.data
}

export async function updateUserContact(userId: number, id: number, input: ContactInput): Promise<UserContactDto> {
  const { data } = await client.put<{ data: UserContactDto }>(`/users/${userId}/contacts/${id}`, input)
  return data.data
}

export async function deleteUserContact(userId: number, id: number): Promise<void> {
  await client.delete(`/users/${userId}/contacts/${id}`)
}
