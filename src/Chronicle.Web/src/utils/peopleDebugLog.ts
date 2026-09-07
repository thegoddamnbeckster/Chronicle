/** Append-only ring buffer for diagnosing the People page's infinite-scroll/jump-recovery
 * timing bugs (2026-09-07: a jump to a letter left the grid showing only its first loaded
 * row, stuck for over a minute, and started working again "the instant" DevTools was opened
 * -- matching the same DevTools-fixes-it fingerprint already root-caused elsewhere in
 * PeopleLibraryPage.tsx for ordinary scroll-triggered loading). That fingerprint means
 * whatever actually happened is gone by the time anyone thinks to look for it live in the
 * console, so this records events into memory continuously regardless of whether DevTools is
 * open, and exposes the buffer on window so it can be read AFTER a stall is noticed instead of
 * requiring the console to have been open and watched the whole time. */
const MAX_ENTRIES = 2000

export interface PeopleDebugEntry {
  t: number
  event: string
  [key: string]: unknown
}

const buffer: PeopleDebugEntry[] = []

export function logPeopleDebug(event: string, data?: Record<string, unknown>) {
  buffer.push({ t: Date.now(), event, ...data })
  if (buffer.length > MAX_ENTRIES) buffer.shift()
  if (typeof window !== 'undefined') {
    (window as unknown as { __peopleDebugLog: PeopleDebugEntry[] }).__peopleDebugLog = buffer
  }
}
