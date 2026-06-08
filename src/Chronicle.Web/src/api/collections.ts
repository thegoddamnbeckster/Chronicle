import client from './client'

export interface CollectionMember {
  id: number
  name: string
  year: number | null
  posterUrl: string | null
  inLibrary: boolean
  libraryStatus: string | null
}

export interface CollectionInfo {
  id: number
  name: string
  posterUrl: string | null
  overview: string | null
  movies: CollectionMember[]
}

export const getCollection = async (mediaItemId: number): Promise<CollectionInfo> => {
  const { data } = await client.get(`/media/${mediaItemId}/collection`)
  return data.data
}
