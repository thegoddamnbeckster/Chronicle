import client from './client'

/** One adjustable number: its default, allowed range and what it is right now. */
export interface Tunable {
  key: string
  group: string
  label: string
  description: string
  unit: string
  default: number
  min: number
  max: number
  value: number
  /** True when an administrator has set a value of their own. */
  isCustom: boolean
}

export async function getTunables(): Promise<Tunable[]> {
  const res = await client.get<Tunable[]>('/settings/tunables')
  return res.data
}

/** A null value puts the default back. */
export async function setTunable(key: string, value: number | null): Promise<void> {
  await client.put(`/settings/tunables/${encodeURIComponent(key)}`, { value })
}
