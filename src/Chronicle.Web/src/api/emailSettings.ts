import client from './client'
import type { ApiResponse } from '@/types'

export interface EmailSettings {
  host: string
  port: number
  /** "none" | "starttls" | "tls" */
  security: string
  username: string | null
  fromAddress: string
  fromName: string
  publicUrl: string | null
  hasPassword: boolean
  isConfigured: boolean
}

export interface EmailSettingsInput {
  host: string
  port: number
  security: string
  username: string
  /** undefined = keep the saved password, '' = remove it. */
  password?: string
  fromAddress: string
  fromName: string
  publicUrl: string
}

export async function getEmailSettings(): Promise<EmailSettings> {
  const { data } = await client.get<ApiResponse<EmailSettings>>('/settings/email')
  return data.data!
}

export async function saveEmailSettings(input: EmailSettingsInput): Promise<EmailSettings> {
  const { data } = await client.put<ApiResponse<EmailSettings>>('/settings/email', input)
  return data.data!
}

export async function sendTestEmail(to: string): Promise<void> {
  await client.post('/settings/email/test', { to })
}
