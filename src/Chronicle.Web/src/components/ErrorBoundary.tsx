import React from 'react'

interface Props {
  children: React.ReactNode
  /** Optional context label shown in the error panel (e.g. "Media Detail"). */
  context?: string
}

interface State {
  error: Error | null
}

/**
 * Catches unhandled render errors in the React tree and displays a friendly
 * recovery panel instead of a blank page.
 *
 * Usage:
 *   <ErrorBoundary context="Media Detail">
 *     <MediaDetailPage />
 *   </ErrorBoundary>
 */
export class ErrorBoundary extends React.Component<Props, State> {
  constructor(props: Props) {
    super(props)
    this.state = { error: null }
  }

  static getDerivedStateFromError(error: Error): State {
    return { error }
  }

  componentDidCatch(error: Error, info: React.ErrorInfo) {
    console.error('[Chronicle] Render error', error, info.componentStack)
  }

  private handleRetry = () => {
    this.setState({ error: null })
  }

  render() {
    const { error } = this.state
    if (!error) return this.props.children

    const { context } = this.props

    return (
      <div style={{
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'center',
        justifyContent: 'center',
        minHeight: '60vh',
        padding: '2rem',
        gap: '1rem',
        color: 'var(--text-primary, #e0e0e0)',
        fontFamily: 'inherit',
      }}>
        <div style={{ fontSize: '2rem' }}>⚠️</div>
        <h2 style={{ margin: 0, fontSize: '1.25rem' }}>
          {context ? `${context} failed to load` : 'Something went wrong'}
        </h2>
        <p style={{ margin: 0, color: 'var(--text-secondary, #aaa)', textAlign: 'center', maxWidth: '480px' }}>
          An unexpected error occurred while rendering this page.
          Try going back or refreshing.
        </p>
        <details style={{
          maxWidth: '600px',
          width: '100%',
          background: 'var(--surface-2, #1a1a2e)',
          border: '1px solid var(--border, #333)',
          borderRadius: '6px',
          padding: '0.75rem 1rem',
          fontSize: '0.8rem',
          color: 'var(--text-secondary, #aaa)',
          cursor: 'pointer',
        }}>
          <summary style={{ userSelect: 'none' }}>Error details</summary>
          <pre style={{ marginTop: '0.5rem', whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
            {error.message}
            {'\n'}
            {error.stack}
          </pre>
        </details>
        <div style={{ display: 'flex', gap: '0.75rem' }}>
          <button
            onClick={() => window.history.back()}
            style={{
              padding: '0.5rem 1.25rem',
              borderRadius: 'var(--radius, 6px)',
              border: '1px solid var(--border)',
              background: 'var(--surface-2)',
              color: 'var(--text-primary)',
              cursor: 'pointer',
              fontSize: '0.9rem',
            }}
          >
            ← Back
          </button>
          <button
            onClick={this.handleRetry}
            style={{
              padding: '0.5rem 1.25rem',
              borderRadius: 'var(--radius, 6px)',
              border: 'none',
              background: 'var(--accent)',
              color: 'var(--accent-fg)',
              cursor: 'pointer',
              fontSize: '0.9rem',
            }}
          >
            Try again
          </button>
        </div>
      </div>
    )
  }
}
