import { useRef, type ReactElement, type ReactNode } from 'react'
import { render } from '@testing-library/react'
import { MemoryRouter, type InitialEntry } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MainScrollContext } from '@/components/layout/Layout'
import { createTestQueryClient } from './test-utils'

/** jsdom lays out every element at 0x0 unless told otherwise, which makes
 * @tanstack/react-virtual compute a viewport of zero rows -- getVirtualItems() would return []
 * regardless of how much data is loaded, so no PersonCard would ever render and every test would
 * look identical whether the fix under test works or not. This stubs layout geometry on the
 * *class prototype* (not the instance -- jsdom's own getBoundingClientRect is what gets called
 * internally by ResizeObserver-derived code paths and offsetHeight readers alike) so a rendered
 * <main> reports a real, scrollable size. Matches the actual mobile Chrome DevTools viewport size
 * this bug was originally reproduced against (375x812) closely enough that a full page of rows
 * fits -- exact pixel values don't matter for what these tests assert. */
export function stubViewportGeometry() {
  Object.defineProperty(HTMLElement.prototype, 'offsetHeight', { configurable: true, value: 700 })
  Object.defineProperty(HTMLElement.prototype, 'offsetWidth', { configurable: true, value: 375 })
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', { configurable: true, value: 700 })
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', { configurable: true, value: 375 })
  // @tanstack/react-virtual's getMaxScrollOffset() is scrollHeight - clientHeight -- jsdom has no
  // real layout engine, so an unstubbed scrollHeight silently defaults to ~clientHeight (0 net),
  // which clamps every scrollToIndex() target back down to 0 regardless of which row it asked
  // for. A large fixed value (comfortably bigger than any row offset these tests scroll to) is
  // all a scroll-container needs to report here; the real value would come from the actual
  // rendered content height, which jsdom never computes.
  Object.defineProperty(HTMLElement.prototype, 'scrollHeight', { configurable: true, value: 999_999 })
  Object.defineProperty(HTMLElement.prototype, 'scrollWidth', { configurable: true, value: 375 })
  HTMLElement.prototype.getBoundingClientRect = function getBoundingClientRect() {
    return {
      width: 375, height: 700, top: 0, left: 0, right: 375, bottom: 700, x: 0, y: 0,
      toJSON() { return this },
    } as DOMRect
  }
}

interface RenderPeoplePageOptions {
  initialEntries?: InitialEntry[]
  queryClient?: QueryClient
}

/** Renders a child inside the same MainScrollContext shape PeopleLibraryPage relies on in
 * production (Layout.tsx's real <main>, not a page-local scroll container -- see that context's
 * own doc comment for why). Returns the <main> node directly so a test can read/set scrollTop
 * and dispatch synthetic 'scroll' events on it exactly like the fix under test does. */
export function renderPeoplePage(ui: ReactElement, options: RenderPeoplePageOptions = {}) {
  const { initialEntries = ['/people'], queryClient = createTestQueryClient() } = options
  let mainEl: HTMLElement | null = null

  function Wrapper({ children }: { children: ReactNode }) {
    const mainRef = useRef<HTMLElement>(null)
    return (
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={initialEntries}>
          <MainScrollContext.Provider value={mainRef}>
            <main ref={mainRef}>{children}</main>
          </MainScrollContext.Provider>
        </MemoryRouter>
      </QueryClientProvider>
    )
  }

  const result = render(ui, { wrapper: Wrapper })
  mainEl = result.container.querySelector('main')
  return { ...result, main: mainEl as HTMLElement, queryClient }
}
