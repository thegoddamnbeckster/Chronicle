import client from './client'
import type { MediaItem } from '@/types'

// ── DTOs ─────────────────────────────────────────────────────────────────────

export interface MediaListDto {
  id: number
  userId: number
  name: string
  description: string | null
  isOrdered: boolean
  itemCount: number
  createdAt: string
  updatedAt: string
}

export interface MediaListItemDto {
  id: number
  position: number
  notes: string | null
  addedAt: string
  mediaItem: MediaItem
}

export interface MediaListDetailDto {
  id: number
  userId: number
  name: string
  description: string | null
  isOrdered: boolean
  createdAt: string
  updatedAt: string
  items: MediaListItemDto[]
}

// ── API calls ─────────────────────────────────────────────────────────────────

export async function getLists(): Promise<MediaListDto[]> {
  const res = await client.get<{ data: MediaListDto[] }>('/lists')
  return res.data.data
}

export async function getList(id: number): Promise<MediaListDetailDto> {
  const res = await client.get<{ data: MediaListDetailDto }>(`/lists/${id}`)
  return res.data.data
}

export async function createList(
  name: string,
  description: string | null,
  isOrdered: boolean,
): Promise<MediaListDto> {
  const res = await client.post<{ data: MediaListDto }>('/lists', { name, description, isOrdered })
  return res.data.data
}

export async function updateList(
  id: number,
  updates: { name?: string; description?: string | null; isOrdered?: boolean },
): Promise<MediaListDto> {
  const res = await client.put<{ data: MediaListDto }>(`/lists/${id}`, updates)
  return res.data.data
}

export async function deleteList(id: number): Promise<void> {
  await client.delete(`/lists/${id}`)
}

export async function addItemToList(
  listId: number,
  mediaItemId: number,
  position?: number,
  notes?: string,
): Promise<MediaListItemDto> {
  const res = await client.post<{ data: MediaListItemDto }>(`/lists/${listId}/items`, {
    mediaItemId,
    position: position ?? 0,
    notes: notes ?? null,
  })
  return res.data.data
}

export async function removeItemFromList(listId: number, itemId: number): Promise<void> {
  await client.delete(`/lists/${listId}/items/${itemId}`)
}

export async function reorderListItems(
  listId: number,
  items: Array<{ itemId: number; position: number }>,
): Promise<void> {
  await client.put(`/lists/${listId}/items/reorder`, { items })
}
