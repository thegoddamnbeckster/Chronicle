import client from './client'

export interface UserPreferences {
  showDiagnostics?: boolean
  defaultFoldsOpen?: boolean
  folds?: Record<string, boolean>
  createCollectionStubs?: boolean
  /** Active theme storage key ("{pluginId}:{themeKey}"), synced across devices. */
  theme?: string
}

export async function getMyPreferences(): Promise<UserPreferences> {
  const { data } = await client.get<{ success: boolean; data: UserPreferences }>('/users/me/preferences')
  return data.data ?? {}
}

export async function updateMyPreferences(prefs: UserPreferences): Promise<void> {
  await client.patch('/users/me/preferences', prefs)
}
