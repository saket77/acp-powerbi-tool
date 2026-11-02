import type { SignedIn } from '../api'

/**
 * What a client sees in Phase 2: proof that they are in, and nothing else.
 *
 * This empty state is the correct one rather than a placeholder. A client with no grant sees no
 * projects, and from Phase 3 that will still be this screen — the list will simply have rows in
 * it when somebody has granted them one.
 */
export function ClientHome({ session }: { session: SignedIn }) {
  return (
    <section className="card">
      <h2>Your Databooks</h2>
      <p className="hint">Signed in as {session.email}.</p>
      <p>
        Nothing has been shared with you yet. When ACP releases a Databook for your project it
        appears here, and only the companies on your own grant are visible inside it.
      </p>
    </section>
  )
}
