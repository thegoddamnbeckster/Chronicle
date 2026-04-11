import client from './client'

export async function updateMyPreferences(prefs: { showDiagnostics?: boolean; defaultFoldsOpen?: boolean; folds?: Record<string, boolean> }): Promise<void> {
  await client.patch('/users/me/preferences', prefs)
}
