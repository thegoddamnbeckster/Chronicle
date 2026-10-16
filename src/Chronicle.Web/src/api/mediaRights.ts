import client from './client'
import type { ApiResponse } from '@/types'

/** Who may delete the library's shared media. */
export async function getMediaRights(): Promise<{ deleteAdminOnly: boolean }> {
  try {
    const { data } = await client.get<ApiResponse<{ deleteAdminOnly: boolean }>>('/media-rights')
    return { deleteAdminOnly: !!data.data?.deleteAdminOnly }
  } catch {
    // Unreadable means "not restricted" here: the server enforces the rule either way, this only decides which buttons to show.
    return { deleteAdminOnly: false }
  }
}

export async function setMediaRights(deleteAdminOnly: boolean): Promise<{ deleteAdminOnly: boolean }> {
  const { data } = await client.put<ApiResponse<{ deleteAdminOnly: boolean }>>('/media-rights', { deleteAdminOnly })
  return { deleteAdminOnly: !!data.data?.deleteAdminOnly }
}
