import { useQuery } from '@tanstack/react-query'
import { getMediaRights } from '@/api/mediaRights'
import { useContext } from 'react'
import { AuthContext } from '@/contexts/AuthContext'

/**
 * Whether the signed-in person may delete shared media. Administrators always may; everyone else unless an administrator has reserved
 * deletion (Settings > Users). While the setting is loading, or if it cannot be read, the buttons stay: the server enforces the rule
 * regardless, this only keeps people from being offered something that would be refused.
 */
export function useCanDeleteMedia(): boolean {
  // Read the context directly: outside a provider (a page rendered on its own) there is simply no restriction to apply.
  const user = useContext(AuthContext)?.user ?? null
  const { data } = useQuery({
    queryKey: ['media-rights'],
    queryFn: getMediaRights,
    staleTime: 60_000,
    retry: false,
    enabled: !!user && !user.isAdmin,
  })
  return !!user?.isAdmin || !data?.deleteAdminOnly
}
