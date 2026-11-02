import type { SignedIn } from '../api'

/**
 * Who you are, and the way out.
 *
 * Signing out is a real form submission rather than a fetch, because the browser has to follow
 * the redirect chain through Microsoft's end-session endpoint and back. A fetch cannot: it is
 * not allowed to read a cross-origin Location header, so the sign-out would stop at our own
 * door and leave the Microsoft session standing.
 */
export function IdentityBar({ session }: { session: SignedIn }) {
  const staff = session.kind === 'AcpStaff'

  return (
    <section className="card">
      <div className="masthead">
        <div>
          <div className="row">
            <strong>{session.displayName}</strong>
            <span className={staff ? 'badge staff' : 'badge'}>
              {staff ? 'ACP staff' : 'Client'}
            </span>
          </div>
          <p className="hint" style={{ margin: 0 }}>
            {session.email}
          </p>
        </div>

        <form method="post" action="/api/auth/logout">
          <button type="submit">Sign out</button>
        </form>
      </div>
    </section>
  )
}
