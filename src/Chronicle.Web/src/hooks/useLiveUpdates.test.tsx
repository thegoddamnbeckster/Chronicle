import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { renderHook, act } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { LIBRARY_REFRESH_MIN_MS, POLL_MS, UNREAD_KEY, applyChanges, useLiveUpdates } from './useLiveUpdates'
import * as api from '@/api/notifications'

vi.mock('@/api/notifications', async importOriginal => {
  const actual = await importOriginal<typeof import('@/api/notifications')>()
  return { ...actual, getChanges: vi.fn() }
})

const res = (over: Partial<api.ChangesResponse> = {}): api.ChangesResponse => ({
  epoch: 'e1', revision: 10, reset: false, changes: [], unreadNotifications: 0, ...over,
})

function setup() {
  const qc = new QueryClient()
  const invalidate = vi.spyOn(qc, 'invalidateQueries')
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  return { qc, invalidate, wrapper }
}

const keysOf = (spy: { mock: { calls: unknown[][] } }): string[] =>
  spy.mock.calls.map((c: unknown[]) => JSON.stringify((c[0] as { queryKey: unknown[] }).queryKey))

describe('applyChanges', () => {
  it('always records the unread count', () => {
    const { qc } = setup()

    applyChanges(qc, res({ unreadNotifications: 4 }), { lastLibraryRefresh: 0 }, 1_000_000)

    expect(qc.getQueryData(UNREAD_KEY)).toBe(4)
  })

  it('does nothing else when nothing changed', () => {
    const { qc, invalidate } = setup()

    applyChanges(qc, res(), { lastLibraryRefresh: 0 }, 1_000_000)

    expect(invalidate).not.toHaveBeenCalled()
  })

  it('refreshes exactly the items that changed, and the library grid', () => {
    const { qc, invalidate } = setup()

    applyChanges(qc, res({ changes: [{ itemId: 7, kind: 'changed' }, { itemId: 9, kind: 'deleted' }] }), { lastLibraryRefresh: 0 }, 1_000_000)

    expect(keysOf(invalidate)).toEqual(['["media",7]', '["media",9]', '["library"]'])
  })

  it('refreshes the big library grid at most once per interval, however busy the changes are', () => {
    const { qc, invalidate } = setup()
    const state = { lastLibraryRefresh: 0 }
    const change = res({ changes: [{ itemId: 1, kind: 'changed' }] })

    applyChanges(qc, change, state, LIBRARY_REFRESH_MIN_MS)
    applyChanges(qc, change, state, LIBRARY_REFRESH_MIN_MS + 5_000)
    applyChanges(qc, change, state, LIBRARY_REFRESH_MIN_MS * 2)

    expect(keysOf(invalidate).filter((k: string) => k === '["library"]')).toHaveLength(2)
    expect(keysOf(invalidate).filter((k: string) => k === '["media",1]')).toHaveLength(3)   // item pages are never held back
  })

  it('a reset refetches the library and every item page, regardless of the throttle', () => {
    const { qc, invalidate } = setup()
    const state = { lastLibraryRefresh: 999_999 }

    applyChanges(qc, res({ reset: true }), state, 1_000_000)

    expect(keysOf(invalidate)).toEqual(['["library"]', '["media"]'])
    expect(state.lastLibraryRefresh).toBe(1_000_000)
  })
})

describe('useLiveUpdates', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    vi.mocked(api.getChanges).mockReset()
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
  })
  afterEach(() => vi.useRealTimers())

  it('takes a baseline first, then asks what changed since it', async () => {
    const { wrapper, qc } = setup()
    vi.mocked(api.getChanges)
      .mockResolvedValueOnce(res({ revision: 10, unreadNotifications: 2 }))
      .mockResolvedValueOnce(res({ revision: 12, changes: [{ itemId: 5, kind: 'changed' }], unreadNotifications: 3 }))
    renderHook(() => useLiveUpdates(true), { wrapper })

    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    expect(api.getChanges).toHaveBeenNthCalledWith(1, undefined, undefined)
    expect(qc.getQueryData(UNREAD_KEY)).toBe(2)

    await act(async () => { await vi.advanceTimersByTimeAsync(POLL_MS) })
    expect(api.getChanges).toHaveBeenNthCalledWith(2, 10, 'e1')
    expect(qc.getQueryData(UNREAD_KEY)).toBe(3)
  })

  it('does not apply the first answer as if it were news', async () => {
    const { wrapper, invalidate } = setup()
    vi.mocked(api.getChanges).mockResolvedValue(res({ changes: [{ itemId: 1, kind: 'changed' }] }))
    renderHook(() => useLiveUpdates(true), { wrapper })

    await act(async () => { await vi.advanceTimersByTimeAsync(0) })

    expect(invalidate).not.toHaveBeenCalled()
  })

  it('keeps polling on the interval', async () => {
    const { wrapper } = setup()
    vi.mocked(api.getChanges).mockResolvedValue(res())
    renderHook(() => useLiveUpdates(true), { wrapper })

    await act(async () => { await vi.advanceTimersByTimeAsync(POLL_MS * 3) })

    expect(api.getChanges).toHaveBeenCalledTimes(4)   // now + 3 intervals
  })

  it('does not poll while the tab is hidden, and catches up the moment it is shown', async () => {
    const { wrapper } = setup()
    vi.mocked(api.getChanges).mockResolvedValue(res())
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' })
    renderHook(() => useLiveUpdates(true), { wrapper })

    await act(async () => { await vi.advanceTimersByTimeAsync(POLL_MS * 3) })
    expect(api.getChanges).not.toHaveBeenCalled()

    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
    await act(async () => { document.dispatchEvent(new Event('visibilitychange')); await vi.advanceTimersByTimeAsync(0) })
    expect(api.getChanges).toHaveBeenCalledTimes(1)
  })

  it('shrugs off a failed poll and tries again', async () => {
    const { wrapper } = setup()
    vi.mocked(api.getChanges).mockRejectedValueOnce(new Error('offline')).mockResolvedValue(res())
    renderHook(() => useLiveUpdates(true), { wrapper })

    await act(async () => { await vi.advanceTimersByTimeAsync(POLL_MS + 10) })

    expect(api.getChanges).toHaveBeenCalledTimes(2)
  })

  it('does nothing when signed out', async () => {
    const { wrapper } = setup()
    renderHook(() => useLiveUpdates(false), { wrapper })

    await act(async () => { await vi.advanceTimersByTimeAsync(POLL_MS * 2) })

    expect(api.getChanges).not.toHaveBeenCalled()
  })

  it('stops when the page goes away', async () => {
    const { wrapper } = setup()
    vi.mocked(api.getChanges).mockResolvedValue(res())
    const { unmount } = renderHook(() => useLiveUpdates(true), { wrapper })
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })

    unmount()
    await act(async () => { await vi.advanceTimersByTimeAsync(POLL_MS * 5) })

    expect(api.getChanges).toHaveBeenCalledTimes(1)
  })
})
