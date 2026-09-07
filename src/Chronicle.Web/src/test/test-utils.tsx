import type { ReactElement, ReactNode } from 'react'
import { render, type RenderOptions } from '@testing-library/react'
import { MemoryRouter, type InitialEntry } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

/** A fresh, retry-free QueryClient per render -- retries would make a mocked-rejection test
 * wait through react-query's backoff before the query settles into an error state, and a
 * fresh instance per test avoids one test's cached data leaking into the next. */
export function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  })
}

interface RenderWithProvidersOptions extends Omit<RenderOptions, 'wrapper'> {
  initialEntries?: InitialEntry[]
  queryClient?: QueryClient
}

/** Wraps a page component with the providers it actually reads from context in real use:
 * react-query (every page fetches through it) and react-router (every page uses at least
 * useLocation/Link). Pages that also read layout-level context (MainScrollContext) bring
 * their own wrapper on top of this one -- see people-test-utils.tsx. */
export function renderWithProviders(
  ui: ReactElement,
  { initialEntries = ['/'], queryClient = createTestQueryClient(), ...options }: RenderWithProvidersOptions = {},
) {
  function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={initialEntries}>{children}</MemoryRouter>
      </QueryClientProvider>
    )
  }
  return { queryClient, ...render(ui, { wrapper: Wrapper, ...options }) }
}
