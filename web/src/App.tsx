import { useEffect, useState } from 'react'
import { api, type HealthReport, type VersionInfo } from './api'
import { StateBoundary, type LoadState } from './shared/LoadState'

/**
 * Phase 1 only proves the loop: the browser reaches the API through the proxy, and the API
 * reaches SQL. Phase 6 replaces this with the client experience from the wireframe.
 */
export function App() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [health, setHealth] = useState<HealthReport | null>(null)
  const [version, setVersion] = useState<VersionInfo | null>(null)

  useEffect(() => {
    let cancelled = false

    Promise.all([api.health(), api.version()])
      .then(([h, v]) => {
        if (cancelled) return
        setHealth(h)
        setVersion(v)
        setState({ kind: 'ready' })
      })
      .catch((error: unknown) => {
        if (cancelled) return
        setState({
          kind: 'error',
          message:
            error instanceof Error
              ? `Could not reach the API: ${error.message}`
              : 'Could not reach the API.',
        })
      })

    return () => {
      cancelled = true
    }
  }, [])

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', padding: '2rem', maxWidth: '44rem' }}>
      <h1 style={{ marginBottom: '0.25rem' }}>ACP Databook Portal</h1>
      <p style={{ color: '#5b6477', marginTop: 0 }}>
        Phase 1 — skeleton. No identity, no Power BI, no grants yet.
      </p>

      <StateBoundary state={state}>
        <dl style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '0.5rem 1.5rem' }}>
          <dt>API</dt>
          <dd style={{ margin: 0 }}>
            {version?.name} · phase {version?.phase} · {version?.environment}
          </dd>

          <dt>Health</dt>
          <dd style={{ margin: 0 }}>
            {health?.status} in {health?.durationMs.toFixed(0)} ms
          </dd>

          <dt>Database</dt>
          <dd style={{ margin: 0 }}>{health?.checks['database']?.status ?? 'unknown'}</dd>
        </dl>
      </StateBoundary>
    </main>
  )
}
