import { describe, it, expect, afterEach } from 'vitest'
import { render, act, screen } from '@testing-library/react'
import { AlphabetScrollIndicator } from './AlphabetScrollIndicator'

afterEach(() => { document.body.innerHTML = '' })

describe('AlphabetScrollIndicator', () => {
  it('finds the current letter among thousands of cards by reading the layout of only a few', async () => {
    // 5,000 cards in a column, 100px each, the page scrolled so card 3,000 sits on the reference line.
    let reads = 0
    const wrap = document.createElement('div')
    for (let i = 0; i < 5000; i++) {
      const el = document.createElement('div')
      el.dataset.letter = String.fromCharCode(65 + Math.floor(i / 200))        // 25 letters, 200 cards each
      el.setAttribute('data-item', '')
      el.getBoundingClientRect = () => { reads++; return { top: (i - 3000) * 100 + 140 } as DOMRect }
      wrap.appendChild(el)
    }
    document.body.appendChild(wrap)

    render(<AlphabetScrollIndicator selector="[data-item]" enabled />)
    await act(async () => {
      window.dispatchEvent(new Event('scroll'))
      await new Promise(r => requestAnimationFrame(() => r(null)))
    })

    expect(await screen.findByText('P')).toBeInTheDocument()                    // card 3000 -> letter index 15 -> 'P'
    expect(reads).toBeLessThan(30)
  })

  it('hears a scroll of the page content container, not only of the window', async () => {
    // Chronicle scrolls inside <main>, whose scroll events never reach a plain window listener: the badge rarely showed.
    const main = document.createElement('main')
    const card = document.createElement('div')
    card.dataset.letter = 'M'
    card.setAttribute('data-item', '')
    card.getBoundingClientRect = () => ({ top: 200 } as DOMRect)
    main.appendChild(card)
    document.body.appendChild(main)

    render(<AlphabetScrollIndicator selector="[data-item]" enabled />)
    await act(async () => {
      main.dispatchEvent(new Event('scroll'))
      await new Promise(r => requestAnimationFrame(() => r(null)))
    })

    expect(await screen.findByText('M')).toBeInTheDocument()
  })

  it('shows the letter of the section the reference line is in, each section having its own alphabet', async () => {
    const wrap = document.createElement('div')
    const makeGroup = (top: number, letters: string[]) => {
      const g = document.createElement('section')
      g.setAttribute('data-group', '')
      g.getBoundingClientRect = () => ({ top } as DOMRect)
      letters.forEach((l, i) => {
        const el = document.createElement('div')
        el.dataset.letter = l
        el.setAttribute('data-item', '')
        el.getBoundingClientRect = () => ({ top: top + 60 + i * 100 } as DOMRect)
        g.appendChild(el)
      })
      wrap.appendChild(g)
    }
    makeGroup(-5000, ['A', 'B', 'C', 'Z'])        // Movies, scrolled far past
    makeGroup(100, ['A', 'B', 'C'])               // Music, its header just above the reference line (140)
    document.body.appendChild(wrap)

    render(<AlphabetScrollIndicator selector="[data-item]" groupSelector="[data-group]" enabled />)
    await act(async () => {
      window.dispatchEvent(new Event('scroll'))
      await new Promise(r => requestAnimationFrame(() => r(null)))
    })

    // within the second group the item at the line is its 'A' (top 160 > 140 so the first item counts), never the first group's 'Z'
    expect(await screen.findByText('A')).toBeInTheDocument()
    expect(screen.queryByText('Z')).not.toBeInTheDocument()
  })

  it('shows nothing while the reference line is in a section with no cards (collapsed)', async () => {
    const g = document.createElement('section')
    g.setAttribute('data-group', '')
    g.getBoundingClientRect = () => ({ top: 100 } as DOMRect)
    document.body.appendChild(g)

    render(<AlphabetScrollIndicator selector="[data-item]" groupSelector="[data-group]" enabled />)
    await act(async () => {
      window.dispatchEvent(new Event('scroll'))
      await new Promise(r => requestAnimationFrame(() => r(null)))
    })

    expect(document.querySelector('[class*=badge]')).toBeNull()
  })
})
