import { useCallback, useEffect, useState } from 'react'
import { api, type PortalUser, type SignedIn } from '../api'
import { StateBoundary, type LoadState } from '../shared/LoadState'
import { InviteClientContact } from './InviteClientContact'

/**
 * What a consultant sees in Phase 2: the people the portal knows about.
 *
 * Phase 3 puts clients, projects and grants above this; Phase 5 adds uploading. For now the
 * useful thing it proves is that an invitation produced a real row of the right kind.
 */
export function ConsultantHome({ session }: { session: SignedIn }) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [users, setUsers] = useState<PortalUser[]>([])

  const load = useCallback(async () => {
    try {
      const people = await api.users()
      setUsers(people)
      setState(people.length === 0 ? { kind: 'empty' } : { kind: 'ready' })
    } catch (error: unknown) {
      setState({
        kind: 'error',
        message: error instanceof Error ? error.message : 'Could not load people.',
      })
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  return (
    <>
      <InviteClientContact authMode={session.authMode} onInvited={() => void load()} />

      <section className="card">
        <h2>People</h2>
        <p className="hint">
          Everyone the portal has met. &ldquo;Not yet&rdquo; means the invitation has not been
          accepted.
        </p>

        <StateBoundary state={state} emptyMessage="Nobody yet. Invite someone above.">
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
              {users.map((user) => (
                <tr key={user.id}>
                  <td>{user.displayName}</td>
                  <td>{user.email}</td>
                  <td>
                    <span className={user.kind === 'AcpStaff' ? 'badge staff' : 'badge'}>
                      {user.kind === 'AcpStaff' ? 'ACP staff' : 'Client'}
                    </span>
                  </td>
                  <td>
                    {user.lastSeenAt
                      ? new Date(user.lastSeenAt).toLocaleString()
                      : <span className="hint">Not yet</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </StateBoundary>
      </section>
    </>
  )
}
