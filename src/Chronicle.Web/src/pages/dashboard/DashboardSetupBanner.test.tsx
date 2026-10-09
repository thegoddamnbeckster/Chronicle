import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen } from '@testing-library/react'
import DashboardPage from './DashboardPage'
import { renderWithProviders } from '@/test/test-utils'
import * as pluginsApi from '@/api/plugins'
import * as stats from '@/api/stats'
import * as library from '@/api/library'
import * as reports from '@/api/reports'
import { useAuth } from '@/hooks/useAuth'
import type { User } from '@/types'

vi.mock('@/api/plugins')
vi.mock('@/api/stats')
vi.mock('@/api/library')
vi.mock('@/hooks/useAuth')
vi.mock('@/api/reports', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/reports')>()
  return { ...actual, getHistoryPage: vi.fn() }
})

const user = (isAdmin: boolean): User => ({
  id: 1, username: 'u', email: null, displayName: null, isAdmin,
  showDiagnostics: false, showNowPlayingBanner: true, showAllCredits: false,
})

beforeEach(() => {
  vi.mocked(stats.getStats).mockResolvedValue(undefined as never)
  vi.mocked(library.getLibrary).mockResolvedValue({ items: [], total: 0 } as never)
  vi.mocked(reports.getHistoryPage).mockResolvedValue([] as never)
})

describe('Dashboard setup banner', () => {
  it('invites an administrator with no plugins to the guided setup', async () => {
    vi.mocked(useAuth).mockReturnValue({ user: user(true), loading: false, logout: vi.fn(), setUser: vi.fn() })
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([])
    renderWithProviders(<DashboardPage />)

    const banner = await screen.findByRole('region', { name: 'Getting started' })
    expect(banner).toHaveTextContent('Chronicle has no plugins yet')
    expect(screen.getByRole('link', { name: 'Start the setup' })).toHaveAttribute('href', '/getting-started')
  })

  it('stays quiet once any plugin is enabled', async () => {
    vi.mocked(useAuth).mockReturnValue({ user: user(true), loading: false, logout: vi.fn(), setUser: vi.fn() })
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([{ id: 1, isEnabled: true } as pluginsApi.PluginDto])
    renderWithProviders(<DashboardPage />)

    await screen.findByRole('heading', { name: 'Dashboard' })
    await vi.waitFor(() => expect(pluginsApi.listPlugins).toHaveBeenCalled())
    expect(screen.queryByRole('region', { name: 'Getting started' })).not.toBeInTheDocument()
  })

  it('treats only disabled plugins as having none', async () => {
    vi.mocked(useAuth).mockReturnValue({ user: user(true), loading: false, logout: vi.fn(), setUser: vi.fn() })
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([{ id: 1, isEnabled: false } as pluginsApi.PluginDto])
    renderWithProviders(<DashboardPage />)

    expect(await screen.findByRole('region', { name: 'Getting started' })).toBeInTheDocument()
  })

  it('never shows an ordinary user a setup they cannot do', async () => {
    vi.mocked(useAuth).mockReturnValue({ user: user(false), loading: false, logout: vi.fn(), setUser: vi.fn() })
    vi.mocked(pluginsApi.listPlugins).mockResolvedValue([])
    renderWithProviders(<DashboardPage />)

    await screen.findByRole('heading', { name: 'Dashboard' })
    expect(screen.queryByRole('region', { name: 'Getting started' })).not.toBeInTheDocument()
    expect(pluginsApi.listPlugins).not.toHaveBeenCalled()
  })
})
