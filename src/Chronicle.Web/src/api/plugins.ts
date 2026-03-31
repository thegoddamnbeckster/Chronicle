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
  /** User-facing hint shown in the Fix Match input. Comes from the plugin manifest. */
  fixMatchHint: string | null
  /** Media type names this plugin can enrich (e.g. ["TV", "Movies"]). Empty when not loaded. */
  supportedMediaTypes: string[] | null
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

export interface PluginHealthResult {
  healthy: boolean | null
  /** Human-readable reason the check failed. Null when healthy. */
  failureReason: string | null
  /** true = unexpected failure (red badge). false = config/auth issue (yellow badge). */
  isCritical: boolean
}

export async function healthCheckPlugin(id: number): Promise<PluginHealthResult> {
  const res = await client.get<{ data: PluginHealthResult }>(`/plugins/${id}/health`)
  return res.data.data
}

export async function updatePluginSettings(
  id: number,
  settings: Record<string, string>,
): Promise<void> {
  await client.put(`/plugins/${id}/settings`, { settings })
}

// SettingType enum values as serialised by the .NET API (integer, not string):
// Text=0, Password=1, Number=2, Boolean=3, Dropdown=4, MultiSelect=5, Url=6, FilePath=7, TextArea=8
export const SettingType = {
  Text: 0,
  Password: 1,
  Number: 2,
  Boolean: 3,
  Dropdown: 4,
  MultiSelect: 5,
  Url: 6,
  FilePath: 7,
  TextArea: 8,
} as const

export type SettingTypeValue = (typeof SettingType)[keyof typeof SettingType]

export interface SettingDefinition {
  key: string
  label: string
  type: SettingTypeValue
  required: boolean
  description?: string
  defaultValue?: string
}

export interface PluginSettingsSchema {
  settings: SettingDefinition[]
}

export async function getPluginSettings(id: number): Promise<Record<string, string>> {
  const res = await client.get<{ data: Record<string, string> }>(`/plugins/${id}/settings`)
  return res.data.data
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
  version: string
}

export async function listCatalog(): Promise<PluginCatalogEntry[]> {
  const res = await client.get<{ data: PluginCatalogEntry[] }>('/plugins/catalog')
  return res.data.data
}

export async function installFromCatalog(pluginId: string): Promise<PluginDto> {
  const res = await client.post<{ data: PluginDto }>(`/plugins/catalog/${pluginId}/install`)
  return res.data.data
}
