import client from './client'

export interface ServiceStatus {
  isInstalled: boolean
  status: 'Running' | 'Stopped' | 'StartPending' | 'StopPending' | 'NotInstalled' | 'NotAvailable' | string
  startType: string
  account: string
  uptime: string | null
}

export async function getServiceStatus(): Promise<ServiceStatus> {
  const res = await client.get<ServiceStatus>('/settings/service')
  return res.data
}

export async function getChangeAccountCommand(
  accountType: string,
  username?: string,
): Promise<string> {
  const params: Record<string, string> = { accountType }
  if (username) params.username = username
  const res = await client.get<{ command: string }>('/settings/service/change-account-command', { params })
  return res.data.command
}
