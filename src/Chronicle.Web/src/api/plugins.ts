import client from './client'

export interface PluginDto {
  id: number
  pluginId: string
  name: string
  version: string
  author: string
  description: string | null
  isEnabled: boolean
  installedAt: string
  updatedAt: string
  /** Favicon URL from the plugin's manifest.json. Null when plugin is not loaded. */
  iconUrl: string | null
}

export async function listPlugins(): Promise<PluginDto[]> {
  const res = await client.get<{ data: PluginDto[] }>('/plugins')
  return res.data.data
}

export async function installPlugin(dllPath: string): Promise<PluginDto> {
  const res = await client.post<{ data: PluginDto }>('/plugins', { dllPath })
  return res.data.data
}

export async function enablePlugin(id: number): Promise<void> {
  await client.post(`/plugins/${id}/enable`)
}

export async function disablePlugin(id: number): Promise<void> {
  await client.post(`/plugins/${id}/disable`)
}

export async function uninstallPlugin(id: number): Promise<void> {
  await client.delete(`/plugins/${id}`)
}

export async function healthCheckPlugin(id: number): Promise<boolean | null> {
  const res = await client.get<{ data: { healthy: boolean | null } }>(`/plugins/${id}/health`)
  return res.data.data.healthy
}

export async function updatePluginSettings(
  id: number,
  settings: Record<string, string>,
): Promise<void> {
  await client.put(`/plugins/${id}/settings`, { settings })
}

export interface SettingDefinition {
  key: string
  label: string
  type: 'string' | 'secret' | 'bool' | 'int'
  required: boolean
  description?: string
  defaultValue?: string
}

export interface PluginSettingsSchema {
  settings: SettingDefinition[]
}

export async function getPluginSettingsSchema(id: number): Promise<PluginSettingsSchema> {
  const res = await client.get<{ data: PluginSettingsSchema }>(`/plugins/${id}/settings-schema`)
  return res.data.data
}

export interface PluginCatalogEntry {
  pluginId: string
  name: string
  description: string
  author: string
  iconUrl: string | null
  githubRepo: string
  assetName: string
  dllName: string
  tags: string[]
  isInstalled: boolean
}

export async function listCatalog(): Promise<PluginCatalogEntry[]> {
  const res = await client.get<{ data: PluginCatalogEntry[] }>('/plugins/catalog')
  return res.data.data
}

export async function installFromCatalog(pluginId: string): Promise<PluginDto> {
  const res = await client.post<{ data: PluginDto }>(`/plugins/catalog/${pluginId}/install`)
  return res.data.data
}
