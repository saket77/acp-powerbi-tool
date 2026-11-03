import { useEffect, useState } from 'react'

/**
 * Routing, in the smallest form that still gives people a URL they can send to a colleague.
 *
 * Hash-based rather than path-based so that a deep link works with no rewrite rule on the
 * server — a consideration that would otherwise follow us into App Service configuration for
 * the sake of three screens. Phase 6 builds the real client experience and can move to paths
 * then; nothing above this hook knows which it is.
 */
export type Route =
  | { kind: 'home' }
  | { kind: 'clients' }
  | { kind: 'client'; slug: string }
  | { kind: 'project'; key: string }

export function useHashRoute(): [Route, (route: Route) => void] {
  const [route, setRoute] = useState<Route>(() => parse(window.location.hash))

  useEffect(() => {
    const onChange = () => setRoute(parse(window.location.hash))
    window.addEventListener('hashchange', onChange)
    return () => window.removeEventListener('hashchange', onChange)
  }, [])

  return [route, (next) => { window.location.hash = toHash(next) }]
}

export function toHash(route: Route): string {
  switch (route.kind) {
    case 'clients':
      return '#/clients'
    case 'client':
      return `#/clients/${encodeURIComponent(route.slug)}`
    case 'project':
      return `#/projects/${encodeURIComponent(route.key)}`
    case 'home':
      return '#/'
  }
}

function parse(hash: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean).map(decodeURIComponent)

  if (parts[0] === 'clients') {
    return parts[1] ? { kind: 'client', slug: parts[1] } : { kind: 'clients' }
  }

  if (parts[0] === 'projects' && parts[1]) return { kind: 'project', key: parts[1] }

  return { kind: 'home' }
}
