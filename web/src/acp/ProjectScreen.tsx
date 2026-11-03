import { useCallback, useEffect, useState } from 'react'
import {
  api,
  ApiError,
  type AcpProjectView,
  type ClientDetail,
  type PortalUser,
  type ProjectRole,
} from '../api'
import { StateBoundary, type LoadState } from '../shared/LoadState'
import { toHash } from '../shared/useHashRoute'

const AcpRoles: ProjectRole[] = ['Lead', 'Member', 'Viewer']

/**
 * One project and everybody who can see it.
 *
 * A 404 here means "no such project, or not yours" and the screen cannot tell which, because
 * the API cannot either — the database removed the row before anything had a chance to ask.
 */
export function ProjectScreen({ projectKey }: { projectKey: string }) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [view, setView] = useState<AcpProjectView | null>(null)
  const [client, setClient] = useState<ClientDetail | null>(null)
  const [people, setPeople] = useState<PortalUser[]>([])

  const load = useCallback(async () => {
    try {
      const project = await api.acpProject(projectKey)
      const [clientDetail, users] = await Promise.all([
        api.client(project.project.clientSlug),
        api.users(),
      ])

      setView(project)
      setClient(clientDetail)
      setPeople(users)
      setState({ kind: 'ready' })
    } catch (e: unknown) {
      // A 404 is the normal answer for a project that is not yours, and it renders exactly like
      // one that does not exist. Anything else is a real failure and says so.
      setState(
        e instanceof ApiError && e.status === 404
          ? { kind: 'noAccess' }
          : { kind: 'error', message: e instanceof Error ? e.message : 'Could not load the project.' },
      )
    }
  }, [projectKey])

  useEffect(() => {
    void load()
  }, [load])

  return (
    <StateBoundary state={state}>
      {view && client && (
        <>
          <section className="card">
            <h2>{view.project.name}</h2>
            <p className="hint">
              <a href={toHash({ kind: 'client', slug: view.project.clientSlug })}>
                ← {view.project.clientName}
              </a>
              {' · '}
              {view.project.status} · ends {view.project.endDate}
            </p>
            <p style={{ marginBottom: 0 }}>
              {view.project.hasReleasedVersion
                ? 'A version is released.'
                : 'Nothing released yet — uploading arrives in Phase 5.'}
            </p>
          </section>

          <AddGrant
            projectKey={projectKey}
            people={people}
            companies={client.companies}
            onChanged={() => void load()}
          />

          <section className="card">
            <h2>Who can see this</h2>
            <p className="hint">
              Removing somebody ends their session on their very next request, not whenever their
              cookie happens to expire.
            </p>

            <table>
              <thead>
                <tr>
                  <th>Person</th>
                  <th>Role</th>
                  <th>Companies</th>
                  <th>Until</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {view.grants.map((grant) => (
                  <tr key={grant.id}>
                    <td>
                      {grant.displayName}
                      <br />
                      <span className="hint">{grant.email}</span>
                    </td>
                    <td>
                      <span className={grant.kind === 'AcpStaff' ? 'badge staff' : 'badge'}>
                        {grant.role}
                      </span>
                    </td>
                    <td>
                      {grant.companies.length === 0 ? (
                        <span className="hint">all</span>
                      ) : (
                        grant.companies.join(', ')
                      )}
                    </td>
                    <td>{grant.endDate ?? <span className="hint">project end</span>}</td>
                    <td>
                      <Revoke
                        projectKey={projectKey}
                        grantId={grant.id}
                        onDone={() => void load()}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>
        </>
      )}
    </StateBoundary>
  )
}

function Revoke({
  projectKey,
  grantId,
  onDone,
}: {
  projectKey: string
  grantId: string
  onDone: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function revoke() {
    setBusy(true)
    setError(null)
    try {
      await api.revokeGrant(projectKey, grantId)
      onDone()
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Could not remove that grant.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <button onClick={() => void revoke()} disabled={busy}>
        {busy ? '…' : 'Remove'}
      </button>
      {error && (
        <p className="error" style={{ margin: '0.3rem 0 0', maxWidth: '18rem' }}>
          {error}
        </p>
      )}
    </>
  )
}

function AddGrant({
  projectKey,
  people,
  companies,
  onChanged,
}: {
  projectKey: string
  people: PortalUser[]
  companies: { id: string; code: string; name: string }[]
  onChanged: () => void
}) {
  const [userId, setUserId] = useState('')
  const [role, setRole] = useState<ProjectRole>('Viewer')
  const [selected, setSelected] = useState<string[]>([])
  const [endDate, setEndDate] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const person = people.find((p) => p.id === userId)
  const isClient = person?.kind === 'ClientUser'

  // The role follows the person rather than the other way round: a client contact can only be
  // a Client, and ACP staff can only be one of the ACP roles. The API enforces it too; this
  // just stops the screen offering a combination that will be refused.
  useEffect(() => {
    if (!person) return
    setRole(person.kind === 'ClientUser' ? 'Client' : 'Viewer')
    setSelected([])
  }, [person])

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)

    try {
      await api.setGrant(projectKey, {
        userId,
        role,
        ...(endDate === '' ? {} : { endDate }),
        ...(isClient ? { companyIds: selected } : {}),
      })
      setUserId('')
      setSelected([])
      setEndDate('')
      onChanged()
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Could not save that grant.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="card">
      <h2>Give somebody access</h2>
      <p className="hint">
        Granting the same person again replaces what they had. Client contacts must be given at
        least one company — that is what their access is made of.
      </p>

      <form onSubmit={(e) => void submit(e)}>
        <div className="fields">
          <div>
            <label htmlFor="grant-person">Person</label>
            <select
              id="grant-person"
              value={userId}
              onChange={(e) => setUserId(e.target.value)}
              required
            >
              <option value="">Choose…</option>
              {people.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.displayName} — {p.kind === 'AcpStaff' ? 'ACP' : 'client'}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label htmlFor="grant-role">Role</label>
            <select
              id="grant-role"
              value={role}
              onChange={(e) => setRole(e.target.value as ProjectRole)}
              disabled={!person || isClient}
            >
              {(isClient ? (['Client'] as ProjectRole[]) : AcpRoles).map((r) => (
                <option key={r} value={r}>
                  {r}
                </option>
              ))}
            </select>
          </div>
        </div>

        {isClient && (
          <div style={{ marginTop: '0.9rem' }}>
            <label>Companies they may see</label>
            <div className="row">
              {companies.map((company) => (
                <label key={company.id} className="row" style={{ gap: '0.3rem', margin: 0 }}>
                  <input
                    type="checkbox"
                    // The visible text sits in the span beside it, which screen readers do pick
                    // up from the wrapping label — but not reliably once this grows a tooltip
                    // and a count. Naming it outright costs nothing.
                    aria-label={`${company.code} — ${company.name}`}
                    checked={selected.includes(company.id)}
                    onChange={(e) =>
                      setSelected((current) =>
                        e.target.checked
                          ? [...current, company.id]
                          : current.filter((id) => id !== company.id),
                      )
                    }
                  />
                  <span title={company.name}>{company.code}</span>
                </label>
              ))}
            </div>
          </div>
        )}

        <div className="fields" style={{ marginTop: '0.9rem' }}>
          <div>
            <label htmlFor="grant-end">Access ends (optional)</label>
            <input
              id="grant-end"
              type="date"
              value={endDate}
              onChange={(e) => setEndDate(e.target.value)}
            />
          </div>
          <div className="row">
            <button className="primary" type="submit" disabled={busy || userId === ''}>
              {busy ? 'Saving…' : 'Save grant'}
            </button>
          </div>
        </div>

        {error && <p className="error">{error}</p>}
      </form>
    </section>
  )
}
