import client from './client'
import type { ApiResponse, AuthResponse, SessionInfo, User } from '@/types'

export async function login(username: string, password: string): Promise<AuthResponse> {
  const { data } = await client.post<ApiResponse<AuthResponse>>('/auth/login', { username, password })
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Login failed')
  return data.data
}

export async function register(username: string, password: string, email?: string): Promise<AuthResponse> {
  const { data } = await client.post<ApiResponse<AuthResponse>>('/auth/register', { username, password, email })
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Registration failed')
  return data.data
}

/** Pulls the server's own explanation out of an error, which the API client has already turned into an Error. */
export function errorText(err: unknown, fallback: string): string {
  return err instanceof Error && err.message ? err.message : fallback
}

/** Asks for a reset link. The server answers the same way whether or not the account exists. */
export async function forgotPassword(identifier: string): Promise<{ emailEnabled: boolean }> {
  const { data } = await client.post<ApiResponse<{ message: string; emailEnabled: boolean }>>('/auth/forgot-password', { identifier })
  return { emailEnabled: !!data.data?.emailEnabled }
}

export async function resetPassword(token: string, newPassword: string): Promise<void> {
  await client.post('/auth/reset-password', { token, newPassword })
}

export async function getMe(): Promise<User> {
  const { data } = await client.get<ApiResponse<User>>('/users/me')
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to get user')
  return data.data
}

/** Ends the calling session on the server. Best-effort: the caller clears local state regardless. */
export async function logoutRequest(): Promise<void> {
  await client.post('/auth/logout')
}

/** Ends every one of the caller's sessions, including this one. */
export async function logoutEverywhere(): Promise<void> {
  await client.post('/auth/logout-all')
}

export async function listMySessions(): Promise<SessionInfo[]> {
  const { data } = await client.get<ApiResponse<SessionInfo[]>>('/auth/sessions')
  return data.data ?? []
}

export async function endMySession(id: string): Promise<void> {
  await client.delete(`/auth/sessions/${id}`)
}

export async function listUserSessions(userId: number): Promise<SessionInfo[]> {
  const { data } = await client.get<ApiResponse<SessionInfo[]>>(`/users/${userId}/sessions`)
  return data.data ?? []
}

export async function endUserSessions(userId: number): Promise<void> {
  await client.delete(`/users/${userId}/sessions`)
}
