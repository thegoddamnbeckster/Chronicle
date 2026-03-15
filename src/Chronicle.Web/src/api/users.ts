import client from './client'

export async function updateMyPreferences(prefs: { showDiagnostics?: boolean }): Promise<void> {
  await client.patch('/users/me/preferences', prefs)
}
