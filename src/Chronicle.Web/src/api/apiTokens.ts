import client from './client'
const apiClient = client

export interface ApiTokenDto {
  id: number
  name: string
  createdAt: string
  lastUsedAt: string | null
  expiresAt: string | null
  /** "full" | "device" | "bridge" - what the key may do. */
  scope: string
}

export interface ApiKeyScopeInfo {
  scope: string
  description: string
}

export interface CreateTokenResponse {
  id: number
  name: string
  token: string   // One-time-visible raw chr_live_… value
  createdAt: string
  expiresAt: string | null
  scope: string
}

export async function listApiTokens(): Promise<ApiTokenDto[]> {
  const res = await apiClient.get<{ data: ApiTokenDto[] }>('/tokens')
  return res.data.data
}

export async function createApiToken(
  name: string,
  expiresAt?: string | null,
  scope?: string,
): Promise<CreateTokenResponse> {
  const res = await apiClient.post<{ data: CreateTokenResponse }>('/tokens', {
    name,
    expiresAt: expiresAt ?? null,
    scope,
  })
  return res.data.data
}

export async function listApiKeyScopes(): Promise<ApiKeyScopeInfo[]> {
  const res = await apiClient.get<{ data: ApiKeyScopeInfo[] }>('/tokens/scopes')
  return res.data.data
}

export async function setApiTokenScope(id: number, scope: string): Promise<void> {
  await apiClient.put(`/tokens/${id}/scope`, { scope })
}

export async function revokeApiToken(id: number): Promise<void> {
  await apiClient.delete(`/tokens/${id}`)
}
