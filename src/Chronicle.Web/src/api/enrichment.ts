import client from './client'

export interface EnrichmentStats {
  pluginId: string
  pluginName: string
  pending: number
  completed: number
  failed: number
  exhausted: number
  notFound: number
  skipped: number
}

export const getEnrichmentStats = async (): Promise<EnrichmentStats[]> => {
  const { data } = await client.get('/enrichment/stats')
  return data.data
}

export const runEnrichment = async (pluginId: string): Promise<void> => {
  await client.post(`/enrichment/${encodeURIComponent(pluginId)}/run`)
}

export const resetEnrichment = async (
  pluginId: string,
  scope: 'exhausted' | 'all'
): Promise<void> => {
  await client.post(`/enrichment/${encodeURIComponent(pluginId)}/reset`, {
    scope,
    mediaItemId: null
  })
}


export interface EnrichmentCandidate {
  title: string | null
  year: number | null
  externalId: string | null
  totalScore: number
  scoreReason: string | null
}

export interface EnrichmentDiagnostics {
  searchQuery: string
  candidatesReturned: number
  threshold?: number
  failureReason?: string | null
  topCandidates: EnrichmentCandidate[]
  scannerSignals?: {
    folderPath: string | null
    hasNfo: boolean
    hasLocalPoster: boolean
    confidenceScore: number | null
  } | null
}

export interface EnrichmentItem {
  enrichmentId: number
  mediaItemId: number
  name: string
  year: number | null
  mediaType: string
  hierarchyLevel: number
  posterUrl: string | null
  externalId: string | null
  status: string
  errorMessage: string | null
  retryCount: number
  maxRetries: number
  lastAttemptedAt: string | null
  diagnostics: EnrichmentDiagnostics | null
  fileScannerMetadata: Record<string, unknown> | null
  parentName: string | null
  grandparentName: string | null
}

export interface EnrichmentItemsPage {
  items: EnrichmentItem[]
  total: number
  page: number
  pageSize: number
  totalPages: number
}

export async function getEnrichmentItems(
  pluginId: string,
  status?: string,
  page = 1,
  pageSize = 25,
  search?: string,
): Promise<EnrichmentItemsPage> {
  const params: Record<string, string | number> = { page, pageSize }
  if (status) params.status = status
  if (search) params.search = search
  const { data } = await client.get(
    `/enrichment/${encodeURIComponent(pluginId)}/items`,
    { params },
  )
  return data.data
}

export async function resetEnrichmentItem(
  pluginId: string,
  mediaItemId: number,
): Promise<void> {
  await client.post(`/enrichment/${encodeURIComponent(pluginId)}/reset`, {
    scope: 'single',
    mediaItemId,
  })
}

export async function skipEnrichmentItem(
  pluginId: string,
  mediaItemId: number,
): Promise<void> {
  await client.post(
    `/enrichment/${encodeURIComponent(pluginId)}/items/${mediaItemId}/skip`,
  )
}
