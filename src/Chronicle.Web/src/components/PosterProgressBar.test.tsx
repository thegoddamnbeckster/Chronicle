import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { PosterProgressBar } from './PosterProgressBar'

describe('PosterProgressBar', () => {
  it('renders a fill at the given percent', () => {
    render(<PosterProgressBar percent={43.79} />)
    const track = screen.getByTitle('44% watched')
    expect(track.querySelector('div')).toHaveStyle({ width: '43.79%' })
  })

  it('clamps a value above 100 to 100', () => {
    render(<PosterProgressBar percent={150} />)
    const track = screen.getByTitle('100% watched')
    expect(track.querySelector('div')).toHaveStyle({ width: '100%' })
  })

  it('clamps a negative value to 0 and renders nothing', () => {
    const { container } = render(<PosterProgressBar percent={-10} />)
    expect(container.firstChild).toBeNull()
  })

  it.each([null, undefined, 0])('renders nothing for percent=%s', (value) => {
    const { container } = render(<PosterProgressBar percent={value} />)
    expect(container.firstChild).toBeNull()
  })
})
