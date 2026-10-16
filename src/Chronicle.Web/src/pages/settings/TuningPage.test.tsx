import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import TuningPage from './TuningPage'
import * as api from '@/api/tunables'

vi.mock('@/api/tunables')

const WATCHED: api.Tunable = {
  key: 'tuning.scrobble.watched_percent', group: 'Scrobbling', label: 'Counts as watched at',
  description: 'A play that reaches this much is marked watched.', unit: '%', default: 80, min: 1, max: 100,
  value: 80, isCustom: false,
}
const RETRY: api.Tunable = {
  key: 'tuning.enrichment.retry_hours', group: 'Metadata', label: 'Retry a failed lookup after',
  description: 'How long a failed lookup rests.', unit: 'hours', default: 24, min: 1, max: 720, value: 48, isCustom: true,
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.getTunables).mockResolvedValue([WATCHED, RETRY])
})

describe('TuningPage', () => {
  it('lists every number under its group, with the default and range', async () => {
    render(<TuningPage />)

    expect(await screen.findByLabelText('Counts as watched at')).toHaveValue(80)
    expect(screen.getByLabelText('Retry a failed lookup after')).toHaveValue(48)
    expect(screen.getByText('Scrobbling')).toBeInTheDocument()
    expect(screen.getByText('Metadata')).toBeInTheDocument()
    expect(screen.getByText(/default 24, 1 to 720.* - changed/)).toBeInTheDocument()
  })

  it('saves a changed number and reloads', async () => {
    vi.mocked(api.setTunable).mockResolvedValue()
    render(<TuningPage />)
    const input = await screen.findByLabelText('Counts as watched at')

    await userEvent.clear(input)
    await userEvent.type(input, '90')
    vi.mocked(api.getTunables).mockResolvedValue([{ ...WATCHED, value: 90, isCustom: true }, RETRY])
    await userEvent.click(screen.getAllByRole('button', { name: 'Save' })[0])

    await waitFor(() => expect(api.setTunable).toHaveBeenCalledWith('tuning.scrobble.watched_percent', 90))
    expect(await screen.findByText('Counts as watched at saved.')).toBeInTheDocument()
  })

  it('will not save a number outside its range', async () => {
    render(<TuningPage />)
    const input = await screen.findByLabelText('Counts as watched at')

    await userEvent.clear(input)
    await userEvent.type(input, '150')

    expect(screen.getAllByRole('button', { name: 'Save' })[0]).toBeDisabled()
  })

  it('puts the default back', async () => {
    vi.mocked(api.setTunable).mockResolvedValue()
    render(<TuningPage />)
    await screen.findByLabelText('Retry a failed lookup after')

    const defaults = screen.getAllByRole('button', { name: 'Default' })
    expect(defaults[0]).toBeDisabled()          // nothing custom on the first
    await userEvent.click(defaults[1])

    await waitFor(() => expect(api.setTunable).toHaveBeenCalledWith('tuning.enrichment.retry_hours', null))
  })
})
