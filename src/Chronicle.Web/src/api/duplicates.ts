import client from './client'

export interface DuplicateCandidate {
  candidateId: number
  itemA: { id: number; name: string; posterUrl: string | null; hierarchyLevel: number; mediaType: string }
  itemB: { id: number; name: string; posterUrl: string | null; hierarchyLevel: number; mediaType: string }
}

export interface MergeHistoryEntry {
  mergeId: number
  loserOriginalId: number
  loserName: string
  mergedAt: string
  mergedByUserId: number | null
}

export async function getDuplicateCandidates(
  page = 1,
  mediaType?: string
): Promise<{ data: DuplicateCandidate[]; pagination: { page: number; perPage: number; total: number | null } }> {
  const params = new URLSearchParams({ page: String(page) })
  if (mediaType) params.set('mediaType', mediaType)
  const res = await client.get(`/duplicates?${params}`)
  return res.data
}

export async function dismissDuplicate(itemAId: number, itemBId: number): Promise<void> {
  await client.post('/duplicates/dismiss', { itemAId, itemBId })
}

export async function triggerDuplicateScan(): Promise<void> {
  await client.post('/duplicates/scan')
}

export async function mergeItems(id: number, targetId: number, winnerId: number): Promise<void> {
  await client.post(`/media/${id}/merge`, { targetId, winnerId })
}

export async function getMergeHistory(id: number): Promise<MergeHistoryEntry[]> {
  const res = await client.get(`/media/${id}/merges`)
  return res.data.data
}

export async function unmergeItem(id: number, mergeId: number): Promise<void> {
  await client.delete(`/media/${id}/merges/${mergeId}`)
}
