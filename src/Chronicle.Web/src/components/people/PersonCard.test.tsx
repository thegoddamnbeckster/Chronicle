import { describe, it, expect } from 'vitest'
import { screen } from '@testing-library/react'
import { PersonCard } from './PersonCard'
import { renderWithProviders } from '@/test/test-utils'
import type { PersonListItem } from '@/types'

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
})
