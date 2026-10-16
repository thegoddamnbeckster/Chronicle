import { describe, it, expect, vi, beforeEach } from 'vitest'
import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import Layout from './Layout'
import { renderWithProviders } from '@/test/test-utils'
import { makeUser } from '@/test/users'

vi.mock('@/api/scan', () => ({ getScanStatus: vi.fn().mockResolvedValue({ available: false, supportedMediaTypeNames: [] }) }))
vi.mock('@/api/diagnostics', () => ({ getDiagnostics: vi.fn().mockResolvedValue({ version: '1', commitHash: 'abc', branch: 'main' }) }))
vi.mock('@/hooks/useLiveUpdates', () => ({ useLiveUpdates: vi.fn() }))
vi.mock('./NotificationBell', () => ({ default: () => null }))
vi.mock('./GlobalSearch', () => ({ default: () => null }))
vi.mock('./NowPlayingBanner', () => ({ default: () => null }))
vi.mock('./ActivityPanel', () => ({ default: () => null }))
vi.mock('./AppFooter', () => ({ default: () => null }))

function renderAt(path: string, isAdmin: boolean) {
  localStorage.clear()
  return renderWithProviders(
    <Routes><Route element={<Layout />}><Route path="*" element={<div>page</div>} /></Route></Routes>,
    { initialEntries: [path], user: makeUser(isAdmin) },
  )
}

const labels = (el: HTMLElement) => within(el).getAllByRole('link').map(a => a.textContent)

beforeEach(() => localStorage.clear())

describe('Settings navigation', () => {
  it('shows the everyday pages first, alphabetically, with the rest folded away', () => {
    renderAt('/', true)

    const settings = screen.getByRole('button', { name: 'Settings' }).parentElement!
    const direct = ['Background Tasks', 'Duplicates', 'Library', 'Plugins']
    for (const l of direct) expect(within(settings).getByRole('link', { name: l })).toBeInTheDocument()

    for (const fold of ['Account', 'Metadata', 'System'])
      expect(screen.getByRole('button', { name: fold })).toHaveAttribute('aria-expanded', 'false')
  })

  it('lists each group alphabetically', async () => {
    renderAt('/', true)

    for (const [fold, expected] of [
      ['Account', ['API Keys', 'Manage Users', 'My Profile', 'Preferences']],
      ['Metadata', ['Field Aliases', 'Media Types', 'Metadata Assignment']],
      ['System', ['Database', 'Email', 'Service', 'Tuning']],
    ] as const) {
      const group = screen.getByRole('button', { name: fold }).parentElement!
      expect(labels(group)).toEqual(expected)
    }
  })

  it('opens the fold that holds the page you are on', () => {
    renderAt('/settings/tuning', true)

    expect(screen.getByRole('button', { name: 'System' })).toHaveAttribute('aria-expanded', 'true')
    expect(screen.getByRole('button', { name: 'Metadata' })).toHaveAttribute('aria-expanded', 'false')
  })

  it('opens a fold when it is clicked, and leaves out what an ordinary person may not use', async () => {
    renderAt('/', false)

    const system = screen.getByRole('button', { name: 'System' })
    await userEvent.click(system)

    expect(system).toHaveAttribute('aria-expanded', 'true')
    const group = system.parentElement!
    expect(labels(group)).toEqual(['Service'])
    expect(screen.queryByRole('link', { name: 'Manage Users' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Media Types' })).not.toBeInTheDocument()
  })
})
