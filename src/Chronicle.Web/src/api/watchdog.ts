import client from './client'
import type { ApiResponse } from '@/types'

export interface WatchdogEvent {
  timeUtc: string
  state: string
  message: string
}

export interface WatchdogStatus {
  enabled: boolean
  /** False when this install cannot run a watchdog (Linux/Docker, or already a Windows service). */
  supported: boolean
  unsupportedReason: string | null
  running: boolean
  /** "off" | "not-running" | "starting" | "healthy" | "restarting" | "stopped" | "gave-up" | "error" */
  state: string
  message: string
  lastCheckUtc: string | null
  lastRestartUtc: string | null
  events: WatchdogEvent[]
}

export async function getWatchdog(): Promise<WatchdogStatus> {
  const { data } = await client.get<ApiResponse<WatchdogStatus>>('/settings/watchdog')
  return data.data!
}

export async function setWatchdogEnabled(enabled: boolean): Promise<WatchdogStatus> {
  const { data } = await client.put<ApiResponse<WatchdogStatus>>('/settings/watchdog', { enabled })
  return data.data!
}
