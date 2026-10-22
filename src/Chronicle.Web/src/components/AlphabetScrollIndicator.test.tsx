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
})
