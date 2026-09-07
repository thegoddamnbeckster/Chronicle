import '@testing-library/jest-dom/vitest'
import { afterEach } from 'vitest'
import { cleanup } from '@testing-library/react'

afterEach(() => {
  cleanup()
})

// jsdom implements neither ResizeObserver nor IntersectionObserver -- both are used throughout
// the app (virtualized grids, lazy-loaded images, sticky headers). A no-op stub is enough for
// tests that don't specifically exercise resize/intersection behavior; a test that DOES care
// should invoke the stored callback itself (see test/virtualizer.ts for the @tanstack/react-
// virtual-specific version of this problem).
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
class IntersectionObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
  takeRecords() { return [] }
  root = null
  rootMargin = ''
  thresholds: number[] = []
}
globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver
globalThis.IntersectionObserver ??= IntersectionObserverStub as unknown as typeof IntersectionObserver

// jsdom implements scrollTo/scrollIntoView as no-ops that still throw "not implemented" to the
// console by default in some versions -- stub them out entirely so a component calling
// element.scrollTo(...) (e.g. useScrollRestoration, PeopleLibraryPage's virtualizer) doesn't spam
// warnings unrelated to what a given test is actually checking.
Element.prototype.scrollTo ??= function scrollTo() {}
Element.prototype.scrollIntoView ??= function scrollIntoView() {}

// matchMedia is used by ThemeContext (prefers-color-scheme) and isn't implemented by jsdom.
window.matchMedia ??= (query: string) => ({
  matches: false,
  media: query,
  onchange: null,
  addListener: () => {},
  removeListener: () => {},
  addEventListener: () => {},
  removeEventListener: () => {},
  dispatchEvent: () => false,
}) as unknown as MediaQueryList
