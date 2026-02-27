import client from './client'
import type { ApiResponse, AuthResponse, User } from '@/types'

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

export async function getMe(): Promise<User> {
  const { data } = await client.get<ApiResponse<User>>('/users/me')
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to get user')
  return data.data
}
