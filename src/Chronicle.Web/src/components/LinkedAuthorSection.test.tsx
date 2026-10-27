import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { LinkedAuthorSection } from './LinkedAuthorSection'

const seen: Array<Record<string, unknown>> = []
vi.mock('@/pages/media/MediaDetailPage', () => ({
  default: (props: Record<string, unknown>) => { seen.push(props); return <div data-testid="record" /> },
}))

describe('LinkedAuthorSection', () => {
  it('embeds the record with nothing but its id, so it shows the poster of its own sources', () => {
    seen.length = 0
    render(<LinkedAuthorSection author={{ itemId: 446193, mediaTypeName: 'Books', isPerson: false } as never} />)

    expect(screen.getByRole('heading', { name: 'Books' })).toBeTruthy()
    expect(seen).toHaveLength(1)
    // No poster is passed down: not the person's photo, not another record's art.
    expect(seen[0]).toEqual({ embeddedId: 446193 })
  })
})
