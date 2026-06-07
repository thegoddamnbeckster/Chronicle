// Themes API — unauthenticated, so we use plain fetch rather than the axios
// client (which redirects to /login on 401). Themes must load even on the
// login page itself.

export interface ThemeDto {
  pluginId:    string
  key:         string
  label:       string
  description: string
  swatches:    [string, string, string]
  variables:   Record<string, string>
}

export async function fetchThemes(): Promise<ThemeDto[]> {
  try {
    const res = await fetch('/api/v1/themes')
    if (!res.ok) return []
    const body = await res.json() as { success: boolean; data: ThemeDto[] }
    return body.success ? (body.data ?? []) : []
  } catch {
    return []
  }
}
