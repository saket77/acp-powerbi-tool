import { useState } from 'react'
import type { GrantView } from '../api'
import { ReportView } from '../shared/ReportView'

/**
 * Previewing a Databook, optionally through a particular client's eyes.
 *
 * "View as" is not a UI convenience. It is the only way a consultant can check that a client
 * will see what they are supposed to before anybody does — and it works by asking the server
 * for a token built from that client's own grant, so what appears here is exactly what they get.
 */
export function PreviewPanel({ projectKey, grants }: { projectKey: string; grants: GrantView[] }) {
  const [viewAs, setViewAs] = useState('')
  const [draft, setDraft] = useState(true)

  const clients = grants.filter((g) => g.role === 'Client')

  return (
    <section className="card">
      <h2>Preview</h2>
      <p className="hint">
        As yourself you see every company. As a client you see exactly what they see — the same
        token, built from their grant.
      </p>

      <div className="fields">
        <div>
          <label htmlFor="view-as">View as</label>
          <select id="view-as" value={viewAs} onChange={(e) => setViewAs(e.target.value)}>
            <option value="">Myself (all companies)</option>
            {clients.map((grant) => (
              <option key={grant.id} value={grant.userId}>
                {grant.displayName} — {grant.companies.join(', ')}
              </option>
            ))}
          </select>
        </div>
        <div>
          <label htmlFor="which">Version</label>
          <select id="which" value={draft ? 'draft' : 'released'} onChange={(e) => setDraft(e.target.value === 'draft')}>
            <option value="draft">Latest draft</option>
            <option value="released">What clients see</option>
          </select>
        </div>
      </div>

      <div style={{ marginTop: '1rem' }}>
        <ReportView
          // Remounting on a change means the renewal timer and the embedded report are both
          // rebuilt for the new identity rather than carrying the old one's token.
          key={`${viewAs}-${draft}`}
          projectKey={projectKey}
          draft={draft}
          {...(viewAs === '' ? {} : { viewAs })}
        />
      </div>
    </section>
  )
}
