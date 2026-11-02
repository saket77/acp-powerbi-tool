import { useEffect, useState } from 'react'
import { api, type VersionInfo } from './api'
import { ConsultantHome } from './acp/ConsultantHome'
import { ClientHome } from './client/ClientHome'
import { SessionProvider, useSession } from './auth/SessionProvider'
import { SignInGate } from './auth/SignInGate'
import { IdentityBar } from './shared/IdentityBar'

export function App() {
  return (
    <main className="page">
      <SessionProvider>
        <Portal />
      </SessionProvider>
    </main>
  )
}

function Portal() {
  const { session } = useSession()
  const [version, setVersion] = useState<VersionInfo | null>(null)

  useEffect(() => {
    api.version().then(setVersion).catch(() => setVersion(null))
  }, [])

  return (
    <>
      <header className="masthead">
        <div>
          <h1>ACP Databook Portal</h1>
          <p>Phase 2 — identity. Sign-in and invitations work; projects and reports do not yet.</p>
        </div>
        {version && (
          <span className="badge">
            phase {version.phase} · {version.environment}
          </span>
        )}
      </header>

      {session.authMode === 'Development' && (
        <div className="banner">
          <strong>Development sign-in.</strong> No password is checked and no Microsoft account is
          involved. Fill in <span className="mono">config/entra.local.json</span> and set{' '}
          <span className="mono">Auth:Mode</span> to <span className="mono">Entra</span> for the
          real thing.
        </div>
      )}

      {!session.authenticated ? (
        <SignInGate session={session} />
      ) : (
        <>
          <IdentityBar session={session} />
          {session.kind === 'AcpStaff' ? (
            <ConsultantHome session={session} />
          ) : (
            <ClientHome session={session} />
          )}
        </>
      )}
    </>
  )
}
