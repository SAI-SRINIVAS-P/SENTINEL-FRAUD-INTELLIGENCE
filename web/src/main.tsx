import React, { Component, type ErrorInfo, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import App from './App'
import './styles.css'

type BoundaryState = { error: string | null }

class AppErrorBoundary extends Component<{ children: ReactNode }, BoundaryState> {
  state: BoundaryState = { error: null }

  static getDerivedStateFromError(error: unknown): BoundaryState {
    return { error: error instanceof Error ? error.message : 'An unexpected rendering error occurred.' }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('Sentinel UI render failure:', error, info.componentStack)
  }

  render() {
    if (this.state.error) {
      return (
        <main className="fatal-error-screen">
          <div className="fatal-error-card">
            <div className="section-kicker">SENTINEL / FRONTEND ERROR</div>
            <h1>The console could not render.</h1>
            <p>The app caught a UI error rather than leaving a blank page. Reload after checking the browser console and API container logs.</p>
            <pre>{this.state.error}</pre>
            <button className="button button-primary" onClick={() => window.location.reload()}>Reload console</button>
          </div>
        </main>
      )
    }
    return this.props.children
  }
}

createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <AppErrorBoundary>
      <App />
    </AppErrorBoundary>
  </React.StrictMode>,
)
