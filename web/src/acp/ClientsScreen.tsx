import { useCallback, useEffect, useState } from 'react'
import { api, type ClientSummary } from '../api'
import { StateBoundary, type LoadState } from '../shared/LoadState'
import { toHash } from '../shared/useHashRoute'

/**
 * ACP's clients. One client is one Power BI workspace and one service principal profile from
 * Phase 4, which is why creating one asks for the company codes up front: a client with no
 * companies can never have a working client grant.
 */
export function ClientsScreen() {
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [clients, setClients] = useState<ClientSummary[]>([])

  const load = useCallback(async () => {
    try {
      const rows = await api.clients()
      setClients(rows)
      setState(rows.length === 0 ? { kind: 'empty' } : { kind: 'ready' })
    } catch (e: unknown) {
      setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load clients.' })
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  return (
    <>
      <NewClient onCreated={() => void load()} />

      <section className="card">
        <h2>Clients</h2>
        <p className="hint">Project counts show the projects you hold a grant on, not every one.</p>

        <StateBoundary state={state} emptyMessage="No clients yet. Add one above.">
          <table>
            <thead>
              <tr>
                <th>Client</th>
                <th>Companies</th>
                <th>Your projects</th>
                <th>Power BI</th>
              </tr>
            </thead>
            <tbody>
              {clients.map((client) => (
                <tr key={client.slug}>
                  <td>
                    <a href={toHash({ kind: 'client', slug: client.slug })}>{client.name}</a>
                  </td>
                  <td>{client.companyCount}</td>
                  <td>{client.projectCount}</td>
                  <td>
                    <span className="badge">{client.provisioned ? 'provisioned' : 'phase 4'}</span>
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

function NewClient({ onCreated }: { onCreated: () => void }) {
  const [name, setName] = useState('')
  const [codes, setCodes] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)

    try {
      await api.createClient({
        name: name.trim(),
        companies: parseCompanies(codes),
      })
      setName('')
      setCodes('')
      onCreated()
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Could not create the client.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="card">
      <h2>Add a client</h2>
      <p className="hint">
        Companies are the <span className="mono">Ges.</span> codes from the FIN Tool. They are
        what a client contact's access is made of, and from Phase 4 what travels inside an embed
        token.
      </p>

      <form onSubmit={(e) => void submit(e)}>
        <div className="fields">
          <div>
            <label htmlFor="client-name">Client name</label>
            <input
              id="client-name"
              type="text"
              required
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Muster Logistik"
            />
          </div>
          <div>
            <label htmlFor="client-codes">Company codes</label>
            <input
              id="client-codes"
              type="text"
              required
              value={codes}
              onChange={(e) => setCodes(e.target.value)}
              placeholder="M01, M02 Muster Nord"
            />
          </div>
        </div>

        <div className="row" style={{ marginTop: '0.9rem' }}>
          <button className="primary" type="submit" disabled={busy || name.trim() === ''}>
            {busy ? 'Creating…' : 'Create client'}
          </button>
          <span className="hint">One per line or comma-separated; a name after the code is optional.</span>
        </div>
        {error && <p className="error">{error}</p>}
      </form>
    </section>
  )
}

/** `M01, M02 Muster Nord` becomes two companies, the second one named. */
function parseCompanies(raw: string): { code: string; name: string }[] {
  return raw
    .split(/[\n,;]+/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .map((line) => {
      const [code, ...rest] = line.split(/\s+/)
      return { code: code ?? line, name: rest.join(' ') }
    })
}
