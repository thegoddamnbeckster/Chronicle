import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import { AdditionalImagesCard } from './AdditionalImagesCard'
import type { MediaItem } from '@/types'

describe('AdditionalImagesCard', () => {
  it('says so when no source has supplied an image, instead of rendering nothing', () => {
    const item = { pluginMetadata: {}, overrides: {} } as unknown as MediaItem
    render(<AdditionalImagesCard item={item} onOpenGallery={vi.fn()} />)
    expect(screen.getByText(/no source has supplied any images/i)).toBeInTheDocument()
  })
})
