import { useCallback, useEffect, useState } from 'react'
import { api, type PortalUser, type ProjectListItem } from '../api'
import { useSession } from '../auth/SessionProvider'
import { StateBoundary, type LoadState } from '../shared/LoadState'
import { toHash } from '../shared/useHashRoute'
import { InviteClientContact } from './InviteClientContact'

/**
 * A consultant's landing screen: the projects they are on, and the people the portal knows.
 *
 * "Your projects" is literal. A consultant sees a project because they hold a grant on it, not
 * because they are staff — the query behind this list is the same one a client's screen runs.
 */
export function ConsultantHome() {
  const { session } = useSession()
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [projects, setProjects] = useState<ProjectListItem[]>([])
  const [people, setPeople] = useState<PortalUser[]>([])

  const load = useCallback(async () => {
    try {
      const [mine, users] = await Promise.all([api.projects(), api.users()])
      setProjects(mine)
      setPeople(users)
      setState({ kind: 'ready' })
    } catch (e: unknown) {
      setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load.' })
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  return (
    <StateBoundary state={state}>
      <section className="card">
        <div className="masthead">
          <div>
            <h2>Your projects</h2>
            <p className="hint">The ones you hold a grant on. Others exist and are not yours.</p>
          </div>
          <a className="button" href={toHash({ kind: 'clients' })}>
            Clients
          </a>
        </div>

        {projects.length === 0 ? (
          <p>
            None yet. Add a client, then a project — you become its lead.
          </p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>Project</th>
                <th>Client</th>
                <th>Status</th>
                <th>Ends</th>
              </tr>
            </thead>
            <tbody>
              {projects.map((project) => (
                <tr key={project.key}>
                  <td>
                    <a href={toHash({ kind: 'project', key: project.key })}>{project.name}</a>
                  </td>
                  <td>{project.clientName}</td>
                  <td>
                    <span className="badge">{project.status}</span>
                  </td>
                  <td>{project.endDate}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {session.authenticated && (
        <InviteClientContact authMode={session.authMode} onInvited={() => void load()} />
      )}

      <section className="card">
        <h2>People</h2>
        <p className="hint">
          Everyone the portal has met. Inviting someone creates a person, not an access — give
          them a project from its own page.
        </p>

        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Email</th>
              <th>Kind</th>
              <th>Signed in</th>
            </tr>
          </thead>
          <tbody>
            {people.map((user) => (
              <tr key={user.id}>
                <td>{user.displayName}</td>
                <td>{user.email}</td>
                <td>
                  <span className={user.kind === 'AcpStaff' ? 'badge staff' : 'badge'}>
                    {user.kind === 'AcpStaff' ? 'ACP staff' : 'Client'}
                  </span>
                </td>
                <td>
                  {user.lastSeenAt ? (
                    new Date(user.lastSeenAt).toLocaleString()
                  ) : (
                    <span className="hint">Not yet</span>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </StateBoundary>
  )
}
