import client, { BACKGROUND_REQUEST } from './client'
import type { ApiResponse } from '@/types'

export interface NotificationItem {
  id: number
  kind: string
  title: string
  body: string | null
  /** An in-app path ("/settings/database"), or null. */
  link: string | null
  createdAt: string
  isRead: boolean
}

export interface NotificationPage {
  unread: number
  items: NotificationItem[]
}

export interface NotificationKindInfo {
  kind: string
  label: string
  description: string
}

export interface ChangedItem {
  itemId: number
  kind: 'changed' | 'deleted' | string
}

export interface ChangesResponse {
  epoch: string
  revision: number
  /** True when the client cannot be told exactly what changed and should refetch. */
  reset: boolean
  changes: ChangedItem[]
  unreadNotifications: number
}

export async function listNotifications(limit = 30): Promise<NotificationPage> {
  const { data } = await client.get<ApiResponse<NotificationPage>>('/notifications', { params: { limit } })
  return data.data ?? { unread: 0, items: [] }
}

export async function listNotificationKinds(): Promise<NotificationKindInfo[]> {
  const { data } = await client.get<ApiResponse<NotificationKindInfo[]>>('/notifications/kinds')
  return data.data ?? []
}

export async function markNotificationRead(id: number): Promise<void> {
  await client.post(`/notifications/${id}/read`)
}

export async function markAllNotificationsRead(): Promise<void> {
  await client.post('/notifications/read-all')
}

export async function deleteNotification(id: number): Promise<void> {
  await client.delete(`/notifications/${id}`)
}

export async function clearReadNotifications(): Promise<void> {
  await client.delete('/notifications/read')
}

/** The background poll: what changed since this revision, plus the unread count. Does not keep a session alive. */
export async function getChanges(since?: number, epoch?: string): Promise<ChangesResponse> {
  const { data } = await client.get<ApiResponse<ChangesResponse>>('/library/changes', {
    ...BACKGROUND_REQUEST,
    params: since === undefined || epoch === undefined ? undefined : { since, epoch },
  })
  return data.data!
}
