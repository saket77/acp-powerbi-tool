import { useEffect, useState } from 'react'
import { api, ApiError, type ProjectDetail } from '../api'
import { StateBoundary, type LoadState } from '../shared/LoadState'
import { ReportView } from '../shared/ReportView'
import { toHash } from '../shared/useHashRoute'

/** One Databook, as a client sees it. */
export function ClientProjectScreen({ projectKey }: { projectKey: string }) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [project, setProject] = useState<ProjectDetail | null>(null)

  useEffect(() => {
    let cancelled = false

    api
      .projectSummary(projectKey)
      .then((p) => {
        if (cancelled) return
        setProject(p)
        setState({ kind: 'ready' })
      })
      .catch((e: unknown) => {
        if (cancelled) return
        // Not yours and does not exist look the same here, because they look the same to the
        // database that answered.
        setState(
          e instanceof ApiError && e.status === 404
            ? { kind: 'noAccess' }
            : { kind: 'error', message: e instanceof Error ? e.message : 'Could not open it.' },
        )
      })

    return () => {
      cancelled = true
    }
  }, [projectKey])

  return (
    <StateBoundary state={state}>
      {project && (
        <section className="card">
          <h2>{project.name}</h2>
          <p className="hint">
            <a href={toHash({ kind: 'home' })}>← Your Databooks</a> · {project.clientName}
          </p>

          <ReportView projectKey={projectKey} />
        </section>
      )}
    </StateBoundary>
  )
}
