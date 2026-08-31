import client from './client'
import type { ApiResponse, PersonListItem, PersonCreditGroup, PersonHeadshot } from '@/types'

export interface GetPeopleParams {
  sort?: 'name' | 'birthDate' | 'createdAt'
  role?: string
  deceased?: boolean
  /** Jumps straight to the first name alphabetically >= this value -- only meaningful when
   * sort is 'name' (see PeopleController.GetPeople). */
  jumpTo?: string
  page?: number
  perPage?: number
}

export async function getPeople(params: GetPeopleParams = {}): Promise<{ items: PersonListItem[]; total: number | null }> {
  const { data } = await client.get<ApiResponse<PersonListItem[]>>('/people', { params })
  return { items: data.data ?? [], total: data.pagination?.total ?? null }
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
