import client from './client'

export interface NfoRebuildQueueDeviceStatus {
  kodiDeviceId: number
  deviceName: string
  /** Null only when the device row has since been deleted -- deviceName falls back to
   * "(deleted device)" in that case too. Shown alongside deviceName because Name has no
   * uniqueness constraint (self-reported by each Kodi instance's own addon settings) -- two
   * genuinely different devices sharing the same name look like a duplicate-row bug without
   * this to tell them apart. */
  host: string | null
  activeClaims: number
  completedCount: number
  /** Refreshed every time this device claims a batch (not just its own 6-hourly
   * re-registration ping) -- a meaningfully fresh "is this thing still on" signal, not
   * lifetime completed counts that don't change whether a device is currently online. Null
   * only when the device row has since been deleted. */
  lastSeenAt: string | null
}

export interface NfoRebuildQueueStatus {
  totalItems: number
  completedCount: number
  pendingCount: number
  activeClaimCount: number
  devices: NfoRebuildQueueDeviceStatus[]
}

export const getNfoRebuildQueueStatus = async (): Promise<NfoRebuildQueueStatus> => {
  const { data } = await client.get('/scraper/nfo-rebuild-queue/status')
  return data.data
}

/** Admin-only -- see KodiDeviceController.ReseedRebuildQueue's own doc. Resets every row
 * (including already-completed ones) back to pending and re-seeds anything new, so the next
 * round of claims from any device covers the whole catalog again. */
export const reseedNfoRebuildQueue = async (): Promise<{ pending: number }> => {
  const { data } = await client.post('/scraper/nfo-rebuild-queue/reseed-all')
  return data.data
}
