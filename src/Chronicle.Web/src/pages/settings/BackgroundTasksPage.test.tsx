import { describe, it, expect, vi, beforeEach } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import BackgroundTasksPage from './BackgroundTasksPage'
import * as tasksApi from '@/api/backgroundTasks'
import * as enrichmentApi from '@/api/enrichment'
import * as scanApi from '@/api/scan'

vi.mock('@/api/backgroundTasks')
vi.mock('@/api/enrichment')
vi.mock('@/api/scan')

const STATS: enrichmentApi.EnrichmentStats = {
  pluginId: 'chronicle.plugin.openlibrary', pluginName: 'Open Library',
  pending: 15, completed: 4420, failed: 0, exhausted: 0, notFound: 3953, skipped: 0,
}

function task(over: Partial<tasksApi.BackgroundTask>): tasksApi.BackgroundTask {
  return {
    taskId: 'chronicle.plugin.openlibrary:fetch-missing-metadata', displayName: 'Fetch Missing Book Metadata', description: '',
    cronExpression: '30 5 * * *', isEnabled: true, isRunning: false, lastRunAt: null, lastRunSucceeded: null, lastErrorMessage: null,
    nextRunAt: null, pluginId: 'chronicle.plugin.openlibrary', pluginName: 'Open Library', pluginIconUrl: null,
    brandColorLight: null, brandColorDark: null, schedulable: true, isRunnable: false, runConfirmation: null, ...over,
  }
}

beforeEach(() => {
  vi.mocked(tasksApi.getBackgroundTasks).mockResolvedValue([])
  vi.mocked(scanApi.getImportProgress).mockResolvedValue(null as never)
})

function renderPage() {
  return render(<MemoryRouter><BackgroundTasksPage /></MemoryRouter>)
}

describe('BackgroundTasksPage -- a plugin blocked for unapproved file changes', () => {
  it('stays in the enrichment table and says why, in place of its counts and buttons', async () => {
    vi.mocked(enrichmentApi.getEnrichmentStats).mockResolvedValue([{ ...STATS, integrityBlockedAt: '2026-10-13T15:17:50Z' }])
    renderPage()

    expect(await screen.findByText('Open Library')).toBeInTheDocument()
    expect(screen.getByText(/Blocked due to unapproved file changes/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Plugins page' })).toHaveAttribute('href', '/plugins')
    expect(screen.queryByRole('button', { name: 'Reset All' })).not.toBeInTheDocument()
    expect(screen.queryByText('4,420')).not.toBeInTheDocument()
  })

  it('a loaded plugin shows its counts and buttons as before', async () => {
    vi.mocked(enrichmentApi.getEnrichmentStats).mockResolvedValue([STATS])
    renderPage()

    expect(await screen.findByText('4,420')).toBeInTheDocument()
    expect(screen.queryByText(/Blocked due to unapproved file changes/)).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Reset All' })).toBeInTheDocument()
  })

  it('marks the plugin\'s group in the scheduled tasks list', async () => {
    vi.mocked(enrichmentApi.getEnrichmentStats).mockResolvedValue([])
    vi.mocked(tasksApi.getBackgroundTasks).mockResolvedValue([task({ pluginIntegrityBlockedAt: '2026-10-13T15:17:50Z' })])
    renderPage()

    expect(await screen.findByText(/Blocked due to unapproved file changes/)).toBeInTheDocument()
  })

  it('does not mark an unblocked plugin\'s group', async () => {
    vi.mocked(enrichmentApi.getEnrichmentStats).mockResolvedValue([])
    vi.mocked(tasksApi.getBackgroundTasks).mockResolvedValue([task({})])
    renderPage()

    expect(await screen.findByText('Open Library')).toBeInTheDocument()
    expect(screen.queryByText(/Blocked due to unapproved file changes/)).not.toBeInTheDocument()
  })
})
