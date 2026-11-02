import { useState } from 'react'
import { api, type AuthMode, type InvitationResult } from '../api'

/**
 * Inviting a client contact.
 *
 * Worth being clear about what this does and does not do: it creates a person, not an access.
 * The invitee can sign in straight away and will see nothing at all until Phase 3 grants them a
 * project. Keeping identity and access apart is deliberate — they are reviewed by different
 * people and revoked for different reasons.
 */
export function InviteClientContact({
  authMode,
  onInvited,
}: {
  authMode: AuthMode
  onInvited: () => void
}) {
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<InvitationResult | null>(null)

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    setResult(null)

    try {
      setResult(await api.inviteClientContact(email.trim(), displayName.trim()))
      setEmail('')
      setDisplayName('')
      onInvited()
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Could not send the invitation.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="card">
      <h2>Invite a client contact</h2>
      <p className="hint">
        Any Microsoft account, work or personal. They sign in as themselves; ACP never issues
        them a password and never buys them a licence.
      </p>

      <form onSubmit={(e) => void submit(e)}>
        <div className="fields">
          <div>
            <label htmlFor="invite-email">Email</label>
            <input
              id="invite-email"
              type="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="anna.keller@example.com"
            />
          </div>
          <div>
            <label htmlFor="invite-name">Name (optional)</label>
            <input
              id="invite-name"
              type="text"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              placeholder="Anna Keller"
            />
          </div>
        </div>

        <div className="row" style={{ marginTop: '0.9rem' }}>
          <button className="primary" type="submit" disabled={busy || email.trim() === ''}>
            {busy ? 'Inviting…' : 'Send invitation'}
          </button>
          {error && <span className="error">{error}</span>}
        </div>
      </form>

      {result && (
        <div className="banner">
          <p style={{ margin: 0 }}>
            <strong className="ok">
              {result.alreadyExisted ? 'Already invited' : 'Invited'}: {result.email}
            </strong>{' '}
            — signed in, they will see nothing until a project is granted to them.
          </p>
          <p style={{ marginBottom: 0 }}>
            {authMode === 'Development'
              ? 'No email was sent: there is no tenant. They now appear in the sign-in picker, which is how you test this.'
              : 'Microsoft has emailed them. If it does not arrive, send them this link:'}
          </p>
          {authMode === 'Entra' && <p className="mono">{result.redeemUrl}</p>}
        </div>
      )}
    </section>
  )
}
