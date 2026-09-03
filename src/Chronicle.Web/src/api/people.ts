import client from './client'
import type { ApiResponse, PersonListItem, PersonCreditGroup, PersonHeadshot } from '@/types'

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

export async function getPersonRoles(): Promise<string[]> {
  const { data } = await client.get<ApiResponse<string[]>>('/people/roles')
  return data.data ?? []
}

export async function getPersonCredits(id: number): Promise<PersonCreditGroup[]> {
  const { data } = await client.get<ApiResponse<PersonCreditGroup[]>>(`/people/${id}/credits`)
  return data.data ?? []
}

export async function getPersonHeadshots(id: number): Promise<PersonHeadshot[]> {
  const { data } = await client.get<ApiResponse<PersonHeadshot[]>>(`/people/${id}/headshots`)
  return data.data ?? []
}
