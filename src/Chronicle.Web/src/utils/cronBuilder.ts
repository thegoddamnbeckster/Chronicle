export type Frequency = 'minutes' | 'hours' | 'daily' | 'weekly' | 'monthly'

export interface ScheduleParams {
  frequency: Frequency
  interval: number        // "every N units" (minutes: 1–59, hours: 1–23)
  timeHour: number        // 0–23 (daily / weekly / monthly)
  timeMinute: number      // 0–59 (daily / weekly / monthly)
  daysOfWeek: number[]    // 0=Sun … 6=Sat (weekly)
  dayOfMonth: number      // 1–31 (monthly)
}

export const DEFAULT_PARAMS: ScheduleParams = {
  frequency: 'hours',
  interval: 4,
  timeHour: 2,
  timeMinute: 0,
  daysOfWeek: [1],  // Monday
  dayOfMonth: 1,
}

/** Convert ScheduleParams → 5-field cron string. */
export function paramsToCron(p: ScheduleParams): string {
  switch (p.frequency) {
    case 'minutes':
      return `*/${p.interval} * * * *`
    case 'hours':
      return `0 */${p.interval} * * *`
    case 'daily':
      return `${p.timeMinute} ${p.timeHour} * * *`
    case 'weekly': {
      const days = p.daysOfWeek.length > 0 ? p.daysOfWeek.join(',') : '1'
      return `${p.timeMinute} ${p.timeHour} * * ${days}`
    }
    case 'monthly':
      return `${p.timeMinute} ${p.timeHour} ${p.dayOfMonth} * *`
  }
}

const MINUTES_RE = /^\*\/(\d+) \* \* \* \*$/
const HOURS_RE   = /^0 \*\/(\d+) \* \* \*$/
const DAILY_RE   = /^(\d+) (\d+) \* \* \*$/
const WEEKLY_RE  = /^(\d+) (\d+) \* \* ([\d,]+)$/
const MONTHLY_RE = /^(\d+) (\d+) (\d+) \* \*$/

/**
 * Parse a cron string into ScheduleParams.
 * Returns null if the expression can't be represented by the visual builder.
 */
export function cronToParams(cron: string): ScheduleParams | null {
  let m: RegExpMatchArray | null

  m = cron.match(MINUTES_RE)
  if (m) return { ...DEFAULT_PARAMS, frequency: 'minutes', interval: parseInt(m[1]) }

  m = cron.match(HOURS_RE)
  if (m) return { ...DEFAULT_PARAMS, frequency: 'hours', interval: parseInt(m[1]) }

  m = cron.match(DAILY_RE)
  if (m) return {
    ...DEFAULT_PARAMS,
    frequency: 'daily',
    timeMinute: parseInt(m[1]),
    timeHour: parseInt(m[2]),
  }

  m = cron.match(WEEKLY_RE)
  if (m) return {
    ...DEFAULT_PARAMS,
    frequency: 'weekly',
    timeMinute: parseInt(m[1]),
    timeHour: parseInt(m[2]),
    daysOfWeek: m[3].split(',').map(Number),
  }

  m = cron.match(MONTHLY_RE)
  if (m) return {
    ...DEFAULT_PARAMS,
    frequency: 'monthly',
    timeMinute: parseInt(m[1]),
    timeHour: parseInt(m[2]),
    dayOfMonth: parseInt(m[3]),
  }

  return null
}

const DOW_NAMES = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

/** Human-readable summary, e.g. "Every 4 hours" */
export function describeSchedule(p: ScheduleParams): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  const time = `${pad(p.timeHour)}:${pad(p.timeMinute)}`

  switch (p.frequency) {
    case 'minutes':
      return p.interval === 1 ? 'Every minute' : `Every ${p.interval} minutes`
    case 'hours':
      return p.interval === 1 ? 'Every hour' : `Every ${p.interval} hours`
    case 'daily':
      return `Daily at ${time}`
    case 'weekly': {
      const days = p.daysOfWeek.map(d => DOW_NAMES[d] ?? d).join(', ')
      return `Every ${days} at ${time}`
    }
    case 'monthly':
      return `Monthly on day ${p.dayOfMonth} at ${time}`
  }
}

/** Validate ScheduleParams. Returns error string or null if valid. */
export function validateParams(p: ScheduleParams): string | null {
  if (p.frequency === 'minutes' && (p.interval < 1 || p.interval > 59))
    return 'Interval must be between 1 and 59 minutes.'
  if (p.frequency === 'hours' && (p.interval < 1 || p.interval > 23))
    return 'Interval must be between 1 and 23 hours.'
  if (p.frequency === 'weekly' && p.daysOfWeek.length === 0)
    return 'Select at least one day of the week.'
  if (p.frequency === 'monthly' && (p.dayOfMonth < 1 || p.dayOfMonth > 31))
    return 'Day of month must be between 1 and 31.'
  return null
}
