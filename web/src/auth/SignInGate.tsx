import { useEffect, useState } from 'react'
import { api, type DevelopmentAccount, type SignedOut } from '../api'
import { useSession } from './SessionProvider'

/**
 * The screen you see before you are anybody.
 *
 * Two shapes, decided by the server rather than by a build flag: a Microsoft button when there
 * is a tenant, and an account picker when there is not. The front end does not know or care
 * which it will get until it asks.
 */
export function SignInGate({ session }: { session: SignedOut }) {
  return session.authMode === 'Entra' ? (
    <MicrosoftSignIn signInUrl={session.signInUrl} />
  ) : (
    <DevelopmentSignIn />
  )
}

function MicrosoftSignIn({ signInUrl }: { signInUrl: string | null }) {
  return (
    <section className="card">
      <h2>Sign in</h2>
      <p className="hint">
        ACP staff and client contacts both sign in with their own Microsoft account. Clients need
        no licence and nothing to install.
      </p>
      <a className="button primary" href={signInUrl ?? '/api/auth/login'}>
        Sign in with Microsoft
      </a>
    </section>
  )
}

function DevelopmentSignIn() {
  const { refresh } = useSession()
  const [accounts, setAccounts] = useState<DevelopmentAccount[] | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .developmentAccounts()
      .then(setAccounts)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Could not load accounts.'))
  }, [])

  async function signIn(key: string) {
    setBusy(key)
    setError(null)
    try {
      await api.developmentSignIn(key)
      await refresh()
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Could not sign in.')
    } finally {
      setBusy(null)
    }
  }

  return (
    <section className="card">
      <h2>Sign in</h2>
      <p className="hint">
        No tenant is configured, so pick somebody. Nobody here declares whether they are staff or
        a client — the portal works that out from the email domain and the directory, using the
        same code a real Microsoft token goes through.
      </p>

      {error && <p className="error">{error}</p>}
      {!accounts && !error && <p role="status">Loading accounts…</p>}

      <ul className="people">
        {accounts?.map((account) => (
          <li key={account.key}>
            <button onClick={() => void signIn(account.key)} disabled={busy !== null}>
              <span className="who">
                <strong>{account.displayName}</strong>
                <small>{account.email}</small>
              </span>
              <span className="badge">
                {account.source === 'invited' ? 'invited here' : account.guest ? 'guest' : 'member'}
              </span>
            </button>
          </li>
        ))}
      </ul>
    </section>
  )
}
