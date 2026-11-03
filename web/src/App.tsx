import { useEffect, useState } from 'react'
import { api, type VersionInfo } from './api'
import { ClientScreen } from './acp/ClientScreen'
import { ClientsScreen } from './acp/ClientsScreen'
import { ConsultantHome } from './acp/ConsultantHome'
import { ProjectScreen } from './acp/ProjectScreen'
import { ClientHome } from './client/ClientHome'
import { SessionProvider, useSession } from './auth/SessionProvider'
import { SignInGate } from './auth/SignInGate'
import { IdentityBar } from './shared/IdentityBar'
import { toHash, useHashRoute } from './shared/useHashRoute'

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
          <h1>
            <a href={toHash({ kind: 'home' })} style={{ color: 'inherit', textDecoration: 'none' }}>
              ACP Databook Portal
            </a>
          </h1>
          <p>Phase 3 — clients, projects and grants. No Power BI yet.</p>
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
          {session.kind === 'AcpStaff' ? <Consultant /> : <ClientHome session={session} />}
        </>
      )}
    </>
  )
}

/** Three screens, chosen by the fragment so a project link can be sent to a colleague. */
function Consultant() {
  const [route] = useHashRoute()

  switch (route.kind) {
    case 'clients':
      return <ClientsScreen />
    case 'client':
      return <ClientScreen slug={route.slug} />
    case 'project':
      return <ProjectScreen projectKey={route.key} />
    case 'home':
      return <ConsultantHome />
  }
}
