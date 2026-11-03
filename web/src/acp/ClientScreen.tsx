import { useCallback, useEffect, useState } from 'react'
import { api, type ClientDetail } from '../api'
import { StateBoundary, type LoadState } from '../shared/LoadState'
import { toHash } from '../shared/useHashRoute'

/** One client: its companies, and the projects on it you can see. */
export function ClientScreen({ slug }: { slug: string }) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [client, setClient] = useState<ClientDetail | null>(null)

  const load = useCallback(async () => {
    try {
      setClient(await api.client(slug))
      setState({ kind: 'ready' })
    } catch (e: unknown) {
      // 404 covers "no such client" and nothing else here — clients are not row-level filtered,
      // only the projects inside them are.
      setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load the client.' })
    }
  }, [slug])

  useEffect(() => {
    void load()
  }, [load])

  return (
    <StateBoundary state={state}>
      {client && (
        <>
          <section className="card">
            <h2>{client.name}</h2>
            <p className="hint">
              <a href={toHash({ kind: 'clients' })}>← All clients</a>
            </p>

            <div className="row">
              {client.companies.map((company) => (
                <span key={company.id} className="badge" title={company.name}>
                  {company.code}
                </span>
              ))}
            </div>
          </section>

          <NewProject clientSlug={slug} onCreated={() => void load()} />

          <section className="card">
            <h2>Projects</h2>
            <p className="hint">Only the ones you hold a grant on. Others exist and are not yours.</p>

            {client.projects.length === 0 ? (
              <p>Nothing here yet.</p>
            ) : (
              <table>
                <thead>
                  <tr>
                    <th>Project</th>
                    <th>Status</th>
                    <th>Ends</th>
                  </tr>
                </thead>
                <tbody>
                  {client.projects.map((project) => (
                    <tr key={project.key}>
                      <td>
                        <a href={toHash({ kind: 'project', key: project.key })}>{project.name}</a>
                      </td>
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
        </>
      )}
    </StateBoundary>
  )
}

function NewProject({ clientSlug, onCreated }: { clientSlug: string; onCreated: () => void }) {
  const [name, setName] = useState('')
  const [endDate, setEndDate] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)

    try {
      await api.createProject({
        clientSlug,
        name: name.trim(),
        ...(endDate === '' ? {} : { endDate }),
      })
      setName('')
      setEndDate('')
      onCreated()
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Could not create the project.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="card">
      <h2>Add a project</h2>
      <p className="hint">
        You become its lead, which is what lets you read it back — row-level security applies to
        consultants exactly as it does to clients.
      </p>

      <form onSubmit={(e) => void submit(e)}>
        <div className="fields">
          <div>
            <label htmlFor="project-name">Project name</label>
            <input
              id="project-name"
              type="text"
              required
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Muster 2026"
            />
          </div>
          <div>
            <label htmlFor="project-end">Ends (optional)</label>
            <input
              id="project-end"
              type="date"
              value={endDate}
              onChange={(e) => setEndDate(e.target.value)}
            />
          </div>
        </div>

        <div className="row" style={{ marginTop: '0.9rem' }}>
          <button className="primary" type="submit" disabled={busy || name.trim() === ''}>
            {busy ? 'Creating…' : 'Create project'}
          </button>
          {error && <span className="error">{error}</span>}
        </div>
      </form>
    </section>
  )
}
