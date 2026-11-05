import { useEffect, useState } from 'react'
import { api, type ProjectListItem, type SignedIn } from '../api'
import { StateBoundary, type LoadState } from '../shared/LoadState'
import { toHash } from '../shared/useHashRoute'

/**
 * What a client sees: their Databooks, and nothing that hints at anybody else's.
 *
 * The list comes from the same endpoint the consultant screens use. Nothing here filters by
 * user — row-level security removed every project this person holds no grant on before the
 * query returned, which is why there is no "if client" anywhere in the portal.
 */
export function ClientHome({ session }: { session: SignedIn }) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [projects, setProjects] = useState<ProjectListItem[]>([])

  useEffect(() => {
    let cancelled = false

    api
      .projects()
      .then((rows) => {
        if (cancelled) return
        setProjects(rows)
        setState(rows.length === 0 ? { kind: 'empty' } : { kind: 'ready' })
      })
      .catch((e: unknown) => {
        if (cancelled) return
        setState({
          kind: 'error',
          message: e instanceof Error ? e.message : 'Could not load your Databooks.',
        })
      })

    return () => {
      cancelled = true
    }
  }, [])

  return (
    <section className="card">
      <h2>Your Databooks</h2>
      <p className="hint">Signed in as {session.email}.</p>

      <StateBoundary
        state={state}
        emptyMessage="Nothing has been shared with you yet. When ACP releases a Databook for your project it appears here."
      >
        <table>
          <thead>
            <tr>
              <th>Databook</th>
              <th>Client</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {projects.map((project) => (
              <tr key={project.key}>
                <td>
                  {project.hasReleasedVersion ? (
                    <a href={toHash({ kind: 'project', key: project.key })}>{project.name}</a>
                  ) : (
                    project.name
                  )}
                </td>
                <td>{project.clientName}</td>
                <td>
                  {project.hasReleasedVersion ? (
                    <span className="badge">ready</span>
                  ) : (
                    <span className="hint">nothing released yet</span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </StateBoundary>
    </section>
  )
}
