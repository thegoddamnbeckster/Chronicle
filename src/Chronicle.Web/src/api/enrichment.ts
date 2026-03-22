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
