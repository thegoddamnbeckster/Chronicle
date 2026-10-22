import client from './client'
import type { ApiResponse, PersonListItem, PersonCreditGroup, PersonAllCredits, PersonHeadshot } from '@/types'

export interface GetPeopleParams {
  role?: string
  deceased?: boolean
  page?: number
  perPage?: number
}

export async function getPeople(params: GetPeopleParams = {}): Promise<{ items: PersonListItem[]; total: number | null }> {
  const { data } = await client.get<ApiResponse<PersonListItem[]>>('/people', { params })
  return { items: data.data ?? [], total: data.pagination?.total ?? null }
}

export interface GetJumpPositionParams {
  /** Same last-name-order comparison GetPeople itself sorts by (see PeopleController.
   * GetOrderedPeopleAsync) -- an A-Z rail letter or arbitrary jump-search text. */
  jumpTo: string
  role?: string
  deceased?: boolean
}

/** Resolves a jump-to-name target to its 0-based position in the full (unfiltered-by-jump)
 * ordered people list, so the caller can compute which GetPeople page to open on instead of
 * GetPeople itself truncating the list down to "target and everything after" -- see
 * PeopleController.GetJumpPosition's own doc for why (per-user request 2026-09-03: scrolling
 * up past a jumped-to person used to be impossible, nothing was ever loaded before them). */
export async function getJumpPosition(params: GetJumpPositionParams): Promise<{ index: number; total: number }> {
  const { data } = await client.get<ApiResponse<{ index: number; total: number }>>('/people/jump-position', { params })
  return data.data ?? { index: 0, total: 0 }
}

export interface PersonNeighbors {
  index: number
  total: number
  prevId: number | null
  nextId: number | null
}

/** Where a person sits in the People list and who is either side, for Prev/Next when not opened from that list. */
export async function getPersonNeighbors(id: number): Promise<PersonNeighbors | null> {
  try {
    const { data } = await client.get<ApiResponse<PersonNeighbors>>(`/people/${id}/neighbors`)
    return data.data ?? null
  } catch {
    return null
  }
}

export async function getPersonRoles(): Promise<string[]> {
  const { data } = await client.get<ApiResponse<string[]>>('/people/roles')
  return data.data ?? []
}

export async function getPersonCredits(id: number): Promise<PersonCreditGroup[]> {
  const { data } = await client.get<ApiResponse<PersonCreditGroup[]>>(`/people/${id}/credits`)
  return data.data ?? []
}

export async function getPersonAllCredits(id: number): Promise<PersonAllCredits> {
  const { data } = await client.get<ApiResponse<PersonAllCredits>>(`/people/${id}/all-credits`)
  return data.data ?? { groups: [], incomplete: false }
}

/** One entry per distinct picture. Hidden photos are left out unless includeHidden is set (the page asks for
 * them so it can offer to restore them). */
export async function getPersonHeadshots(id: number, includeHidden = false): Promise<PersonHeadshot[]> {
  const { data } = await client.get<ApiResponse<PersonHeadshot[]>>(`/people/${id}/headshots`, {
    params: includeHidden ? { includeHidden: true } : undefined,
  })
  return data.data ?? []
}

/** Hides a photo from this person's gallery (e.g. a stranger's picture in the provider's gallery). The photo
 * currently shown cannot be hidden. */
export async function hidePersonHeadshot(personId: number, headshotId: number): Promise<void> {
  await client.post(`/people/${personId}/headshots/${headshotId}/hide`)
}

/** Brings back every photo hidden from this person's gallery. */
export async function restorePersonHeadshots(personId: number): Promise<void> {
  await client.post(`/people/${personId}/headshots/restore`)
}
