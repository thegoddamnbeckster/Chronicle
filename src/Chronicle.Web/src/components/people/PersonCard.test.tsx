import { describe, it, expect, vi, beforeEach } from 'vitest'
import { fireEvent, screen, waitFor } from '@testing-library/react'
import { PersonCard } from './PersonCard'
import { renderWithProviders } from '@/test/test-utils'
import type { PersonListItem } from '@/types'

vi.mock('@/utils/logoGround', () => ({ measureGround: vi.fn() }))
import { measureGround } from '@/utils/logoGround'

beforeEach(() => { vi.mocked(measureGround).mockReset(); vi.mocked(measureGround).mockResolvedValue(null) })

function makePerson(overrides: Partial<PersonListItem> = {}): PersonListItem {
  return {
    id: 1, name: 'Ted Danson', posterUrl: null, birthDate: '1947-12-29T00:00:00Z', deathDate: null,
    roles: ['Actor'], characterName: 'Sam Malone', ...overrides,
  } as PersonListItem
}

describe('PersonCard', () => {
  it('on a title cast tile (fullName) shows only the position -- the character -- with no "Actor", "as" or years', () => {
    renderWithProviders(<PersonCard person={makePerson()} fullName />)

    expect(screen.getByText('Sam Malone')).toBeInTheDocument()
    expect(screen.queryByText(/^Actor$/)).not.toBeInTheDocument()
    expect(screen.queryByText(/^as /)).not.toBeInTheDocument()
    expect(screen.queryByText(/1947/)).not.toBeInTheDocument()
  })

  it('on a title cast tile, a non-actor keeps their role as the position', () => {
    renderWithProviders(<PersonCard person={makePerson({ roles: ['Director'], characterName: null })} fullName />)

    expect(screen.getByText('Director')).toBeInTheDocument()
  })

  it('on the catalog-wide People grid keeps the roles and the birth year, which belong to the person', () => {
    renderWithProviders(<PersonCard person={makePerson()} />)

    expect(screen.getByText('Actor')).toBeInTheDocument()
    expect(screen.getByText('1947')).toBeInTheDocument()
  })

  function loadImage(w: number, h: number) {
    const img = document.querySelector('img') as HTMLImageElement   // hidden until it loads, so not found by role
    Object.defineProperty(img, 'naturalWidth', { value: w, configurable: true })
    Object.defineProperty(img, 'naturalHeight', { value: h, configurable: true })
    fireEvent.load(img)
    return img
  }

  it('shows a wide picture (a company logo) whole on a light ground instead of cropping it', () => {
    const { container } = renderWithProviders(<PersonCard person={makePerson({ name: 'Gearbox Software', posterUrl: 'https://x.test/logo.png', roles: ['Developer'], characterName: null })} fullName />)
    loadImage(213, 160)
    expect(container.querySelector('[class*="posterContain"]')).not.toBeNull()
  })

  it('shows a square logo whole too, since cropping it to a portrait tile would cut it', () => {
    const { container } = renderWithProviders(<PersonCard person={makePerson({ posterUrl: 'https://x.test/logo.png' })} fullName />)
    loadImage(1200, 1196)
    expect(container.querySelector('[class*="posterContain"]')).not.toBeNull()
  })

  it('puts a light logo on a dark ground, by measuring the picture', async () => {
    vi.mocked(measureGround).mockResolvedValueOnce('dark')
    const { container } = renderWithProviders(<PersonCard person={makePerson({ posterUrl: 'https://x.test/white-logo.png' })} fullName />)
    loadImage(300, 150)
    await waitFor(() => expect(container.querySelector('[class*="posterContainDark"]')).not.toBeNull())
    expect(measureGround).toHaveBeenCalledWith('https://x.test/white-logo.png')
  })

  it('keeps a dark logo on the light ground', async () => {
    vi.mocked(measureGround).mockResolvedValueOnce('light')
    const { container } = renderWithProviders(<PersonCard person={makePerson({ posterUrl: 'https://x.test/black-logo.png' })} fullName />)
    loadImage(300, 150)
    await waitFor(() => expect(measureGround).toHaveBeenCalled())
    expect(container.querySelector('[class*="posterContainDark"]')).toBeNull()
    expect(container.querySelector('[class*="posterContain"]')).not.toBeNull()
  })

  it('keeps the light ground when the picture cannot be measured', async () => {
    vi.mocked(measureGround).mockResolvedValueOnce(null)
    const { container } = renderWithProviders(<PersonCard person={makePerson({ posterUrl: 'https://x.test/logo.png' })} fullName />)
    loadImage(300, 150)
    await waitFor(() => expect(measureGround).toHaveBeenCalled())
    expect(container.querySelector('[class*="posterContainDark"]')).toBeNull()
    expect(container.querySelector('[class*="posterContain"]')).not.toBeNull()
  })

  it('does not measure a portrait photo at all', () => {
    vi.mocked(measureGround).mockClear()
    renderWithProviders(<PersonCard person={makePerson({ posterUrl: 'https://x.test/face.jpg' })} fullName />)
    loadImage(500, 750)
    expect(measureGround).not.toHaveBeenCalled()
  })

  it('keeps a portrait photo filling the tile', () => {
    const { container } = renderWithProviders(<PersonCard person={makePerson({ posterUrl: 'https://x.test/face.jpg' })} fullName />)
    loadImage(500, 750)
    expect(container.querySelector('[class*="posterContain"]')).toBeNull()
  })
})
