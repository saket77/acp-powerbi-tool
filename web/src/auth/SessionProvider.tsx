import { createContext, useCallback, useContext, useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { fetchSession, type Session } from '../api'
import { StateBoundary, type LoadState } from '../shared/LoadState'

interface SessionContextValue {
  session: Session
  /** Call after anything that could change who you are, or what you may see. */
  refresh: () => Promise<void>
}

const SessionContext = createContext<SessionContextValue | null>(null)

/**
 * Holds the answer to "who am I" for the whole app.
 *
 * It is re-asked rather than remembered across reloads, because the server can end a session at
 * any moment — a changed grant bumps the security stamp and the very next request is a 401. A
 * cached "signed in" in the browser would just be a stale opinion.
 */
export function SessionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [session, setSession] = useState<Session | null>(null)

  const refresh = useCallback(async () => {
    try {
      setSession(await fetchSession())
      setState({ kind: 'ready' })
    } catch (error: unknown) {
      setState({
        kind: 'error',
        message:
          error instanceof Error
            ? `Could not reach the portal: ${error.message}`
            : 'Could not reach the portal.',
      })
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  return (
    <StateBoundary state={state}>
      {session && (
        <SessionContext.Provider value={{ session, refresh }}>{children}</SessionContext.Provider>
      )}
    </StateBoundary>
  )
}

export function useSession(): SessionContextValue {
  const value = useContext(SessionContext)
  if (!value) throw new Error('useSession must be used inside a SessionProvider')
  return value
}
