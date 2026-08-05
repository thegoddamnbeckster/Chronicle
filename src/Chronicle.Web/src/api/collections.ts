import client from './client'

export interface CollectionMember {
  id: number
  name: string
  year: number | null
  posterUrl: string | null
  inLibrary: boolean
  libraryStatus: string | null
  rating: number | null
  userRating: number | null
  userRatingSource: string | null
  isStub: boolean
}

export interface CollectionInfo {
  id: number
  name: string
  posterUrl: string | null
  overview: string | null
  movies: CollectionMember[]
  /** False for manually-created, non-movie-like collections — there's no external
   *  source (e.g. TMDB) to rebuild membership against. */
  supportsRebuild: boolean
}

export const getCollection = async (mediaItemId: number): Promise<CollectionInfo> => {
  const { data } = await client.get(`/media/${mediaItemId}/collection`)
  return data.data
}

export interface RebuildResult {
  summary: string
  collection: CollectionInfo | null
}

export const rebuildCollection = async (collectionId: number): Promise<RebuildResult> => {
  const { data } = await client.post(`/media/${collectionId}/rebuild-collection`)
  return data.data
}
