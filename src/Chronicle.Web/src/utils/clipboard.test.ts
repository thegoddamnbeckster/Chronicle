import { afterEach, describe, expect, it, vi } from 'vitest'
import { copyText } from './clipboard'

describe('copyText', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('uses the clipboard API where the page is a secure context', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined)
    vi.stubGlobal('navigator', { clipboard: { writeText } })
    vi.stubGlobal('window', { isSecureContext: true })

    expect(await copyText('chr_live_x')).toBe(true)
    expect(writeText).toHaveBeenCalledWith('chr_live_x')
  })

  it('falls back to a selected textarea on plain http, where navigator.clipboard is missing', async () => {
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('window', { isSecureContext: false })
    const area = { value: '', style: {} as Record<string, string>, setAttribute: vi.fn(), select: vi.fn(), setSelectionRange: vi.fn() }
    const body = { appendChild: vi.fn(), removeChild: vi.fn() }
    const execCommand = vi.fn().mockReturnValue(true)
    vi.stubGlobal('document', { createElement: vi.fn().mockReturnValue(area), body, execCommand })

    expect(await copyText('chr_live_x')).toBe(true)
    expect(area.value).toBe('chr_live_x')
    expect(execCommand).toHaveBeenCalledWith('copy')
    expect(body.removeChild).toHaveBeenCalledWith(area)
  })

  it('falls back when the clipboard API refuses', async () => {
    vi.stubGlobal('navigator', { clipboard: { writeText: vi.fn().mockRejectedValue(new Error('denied')) } })
    vi.stubGlobal('window', { isSecureContext: true })
    const area = { value: '', style: {} as Record<string, string>, setAttribute: vi.fn(), select: vi.fn(), setSelectionRange: vi.fn() }
    vi.stubGlobal('document', { createElement: vi.fn().mockReturnValue(area), body: { appendChild: vi.fn(), removeChild: vi.fn() }, execCommand: vi.fn().mockReturnValue(true) })

    expect(await copyText('abc')).toBe(true)
  })

  it('reports failure when nothing could copy', async () => {
    vi.stubGlobal('navigator', {})
    vi.stubGlobal('window', { isSecureContext: false })
    const area = { value: '', style: {} as Record<string, string>, setAttribute: vi.fn(), select: vi.fn(), setSelectionRange: vi.fn() }
    vi.stubGlobal('document', { createElement: vi.fn().mockReturnValue(area), body: { appendChild: vi.fn(), removeChild: vi.fn() }, execCommand: vi.fn().mockReturnValue(false) })

    expect(await copyText('abc')).toBe(false)
  })
})
