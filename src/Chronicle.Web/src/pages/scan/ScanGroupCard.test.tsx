import { describe, it, expect, vi } from 'vitest'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import ScanGroupCard, { groupToPayload, relatedFileCount } from './ScanGroupCard'
import type { ScanGroupDto } from '@/types'

const group = (over: Partial<ScanGroupDto> = {}): ScanGroupDto => ({
  groupKey: 'heat', name: 'Heat', hierarchyLevel: 0, year: 1995, number: null, posterPath: null, confidenceScore: 90,
  signalSources: ['folder'], hasConflicts: false, children: [], files: ['C:/m/Heat/Heat.mkv'], folderPath: 'C:/m/Heat',
  author: null, series: null, ...over,
})

describe('ScanGroupCard', () => {
  it('counts related files across the group and its children', () => {
    const g = group({ relatedFiles: ['a.srt'], children: [group({ groupKey: 'c', relatedFiles: ['b.jpg', 'c.jpg'] })] })

    expect(relatedFileCount(g)).toBe(3)
    expect(relatedFileCount(group())).toBe(0)
  })

  it('shows how many related files came with the item', () => {
    render(<ScanGroupCard group={group({ relatedFiles: ['a.srt', 'b.jpg'] })} checked onToggle={vi.fn()} />)

    expect(screen.getByText('+2 related files')).toBeInTheDocument()
  })

  it('shows nothing about related files or a type mismatch for an ordinary group', () => {
    render(<ScanGroupCard group={group()} checked onToggle={vi.fn()} />)

    expect(screen.queryByText(/related file/)).not.toBeInTheDocument()
    expect(screen.queryByText(/looks like/)).not.toBeInTheDocument()
  })

  it('flags a group that looks like another type and offers to switch and rescan', async () => {
    const onSwitch = vi.fn()
    render(<ScanGroupCard
      group={group({ suggestedMediaTypeId: 4, suggestedMediaTypeName: 'TV Shows', suggestedMediaTypeReason: '100% of the files look like TV Shows' })}
      checked onToggle={vi.fn()} onSwitchType={onSwitch} />)

    expect(screen.getByText(/looks like TV Shows/)).toHaveAttribute('title', '100% of the files look like TV Shows')
    await userEvent.click(screen.getByRole('button', { name: 'Switch to TV Shows and rescan' }))

    expect(onSwitch).toHaveBeenCalledWith(4)
  })

  it('still warns, without a button, when this type cannot be scanned here', () => {
    render(<ScanGroupCard group={group({ suggestedMediaTypeId: 4, suggestedMediaTypeName: 'TV Shows' })} checked onToggle={vi.fn()} />)

    expect(screen.getByText(/looks like TV Shows/)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Switch to/ })).not.toBeInTheDocument()
  })

  it('carries the related files into the import payload', () => {
    const payload = groupToPayload(group({ relatedFiles: ['a.srt'], children: [group({ groupKey: 'c', relatedFiles: ['b.jpg'] })] }))

    expect(payload.relatedFiles).toEqual(['a.srt'])
    expect(payload.children[0].relatedFiles).toEqual(['b.jpg'])
    expect(groupToPayload(group()).relatedFiles).toEqual([])
  })

  it('names the type an automatic-detect scan sorted the group into, and carries it into the import', () => {
    const g = group({ mediaTypeId: 3, mediaTypeName: 'Music' })
    render(<ScanGroupCard group={g} checked onToggle={vi.fn()} />)

    expect(screen.getByText('Music')).toBeInTheDocument()
    expect(groupToPayload(g).mediaTypeId).toBe(3)
    expect(groupToPayload(group()).mediaTypeId).toBeNull()
  })
})
