import client from './client'
const apiClient = client

export interface ApiTokenDto {
  id: number
  name: string
  createdAt: string
  lastUsedAt: string | null
  expiresAt: string | null
}

export interface CreateTokenResponse {
  id: number
  name: string
  token: string   // One-time-visible raw chr_live_… value
  createdAt: string
  expiresAt: string | null
}

export async function listApiTokens(): Promise<ApiTokenDto[]> {
  const res = await apiClient.get<{ data: ApiTokenDto[] }>('/tokens')
  return res.data.data
}

export async function createApiToken(
  name: string,
  expiresAt?: string | null,
): Promise<CreateTokenResponse> {
  const res = await apiClient.post<{ data: CreateTokenResponse }>('/tokens', {
    name,
    expiresAt: expiresAt ?? null,
  })
  return res.data.data
}

export async function revokeApiToken(id: number): Promise<void> {
  await apiClient.delete(`/tokens/${id}`)
}
