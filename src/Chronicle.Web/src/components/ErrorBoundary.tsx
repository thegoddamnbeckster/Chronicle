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
        fontFamily: 'inherit',
      }}>
        {/* Card — always white background with dark text, immune to theme */}
        <div style={{
          background: '#ffffff',
          color: '#111827',
          borderRadius: '10px',
          boxShadow: '0 4px 24px rgba(0,0,0,0.35)',
          padding: '2rem 2.5rem',
          maxWidth: '600px',
          width: '100%',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          gap: '1rem',
        }}>
          <div style={{ fontSize: '2rem', lineHeight: 1 }}>⚠️</div>
          <h2 style={{ margin: 0, fontSize: '1.25rem', color: '#111827', textAlign: 'center' }}>
            {context ? `${context} failed to load` : 'Something went wrong'}
          </h2>
          <p style={{ margin: 0, color: '#4b5563', textAlign: 'center', maxWidth: '420px', lineHeight: 1.5 }}>
            An unexpected error occurred while rendering this page.
            Try going back or refreshing.
          </p>
          <details style={{
            width: '100%',
            background: '#f3f4f6',
            border: '1px solid #d1d5db',
            borderRadius: '6px',
            padding: '0.75rem 1rem',
            fontSize: '0.78rem',
            color: '#374151',
            cursor: 'pointer',
          }}>
            <summary style={{ userSelect: 'none', color: '#374151' }}>Error details</summary>
            <pre style={{ marginTop: '0.5rem', whiteSpace: 'pre-wrap', wordBreak: 'break-all', color: '#1f2937' }}>
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
                borderRadius: '6px',
                border: '1px solid #d1d5db',
                background: '#ffffff',
                color: '#374151',
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
                borderRadius: '6px',
                border: 'none',
                background: '#2563eb',
                color: '#ffffff',
                cursor: 'pointer',
                fontSize: '0.9rem',
              }}
            >
              Try again
            </button>
          </div>
        </div>
      </div>
    )
  }
}
