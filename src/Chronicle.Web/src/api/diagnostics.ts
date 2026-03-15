import client from './client'

export interface DiagnosticsInfo {
  repoRoot: string
  apiProjectPath: string
  apiDir: string
  dbPath: string
  dbExists: boolean
  logsPath: string
  branch: string
  commitHash: string
  apiUrl: string
  webUrl: string
  version: string
}

export async function getDiagnostics(): Promise<DiagnosticsInfo> {
  const res = await client.get<{ success: true; data: DiagnosticsInfo }>('/diagnostics')
  return res.data.data
}
