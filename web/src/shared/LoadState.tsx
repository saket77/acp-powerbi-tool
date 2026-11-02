import type { ReactNode } from 'react'

/**
 * The six states every data-bearing screen has to handle.
 *
 * Taken from the wireframe, which builds a state switcher into the client report view rather
 * than leaving these to chance. They are listed here from the first commit so that no screen
 * gets written with only a happy path and an afterthought spinner.
 */
export type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready' }
  | { kind: 'empty' }
  | { kind: 'error'; message: string }
  | { kind: 'busy'; message: string }
  /** Signed in, but not granted this project. Renders the same as a missing one, on purpose. */
  | { kind: 'noAccess' }

interface Props {
  state: LoadState
  children: ReactNode
  emptyMessage?: string
}

export function StateBoundary({ state, children, emptyMessage }: Props) {
  switch (state.kind) {
    case 'ready':
      return <>{children}</>
    case 'loading':
      return <p role="status">Loading…</p>
    case 'busy':
      return <p role="status">{state.message}</p>
    case 'empty':
      return <p>{emptyMessage ?? 'Nothing here yet.'}</p>
    case 'error':
      return <p role="alert">{state.message}</p>
    case 'noAccess':
      // Deliberately indistinguishable from "does not exist". Telling someone a project exists
      // but is not theirs is itself a disclosure.
      return <p role="alert">Not found.</p>
  }
}
