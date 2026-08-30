import client from './client'
import type { ApiResponse, PersonListItem, PersonCreditGroup } from '@/types'

export interface GetPeopleParams {
  sort?: 'name' | 'birthDate' | 'createdAt'
  role?: string
  deceased?: boolean
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
