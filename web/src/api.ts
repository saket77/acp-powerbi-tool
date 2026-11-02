/**
 * The one place that talks to the backend.
 *
 * Every call is same-origin through the Vite proxy in development and through the same host in
 * production, and every call sends credentials, because the session is an HttpOnly cookie the
 * JavaScript here can neither read nor set. That is the whole reason this file has no notion of
 * a token: there is nothing here to put in an Authorization header.
 */

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

const json = { Accept: 'application/json' }

async function get<T>(path: string): Promise<T> {
  const response = await fetch(path, { credentials: 'same-origin', headers: json })

  if (!response.ok) {
    // 404 covers both "no such thing" and "not yours". The UI must not try to tell them apart,
    // because the backend deliberately does not.
    throw new ApiError(response.status, `${response.status} ${response.statusText}`)
  }

  return (await response.json()) as T
}

async function post<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(path, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { ...json, 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  })

  if (!response.ok) throw new ApiError(response.status, await describe(response))

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}

/**
 * ASP.NET Core answers a rejected request with a problem document. Reading the sentence out of
 * it is the difference between "409" and "that address belongs to ACP staff" on the screen.
 */
async function describe(response: Response): Promise<string> {
  try {
    const problem = (await response.json()) as {
      detail?: string
      title?: string
      errors?: Record<string, string[]>
    }

    const firstFieldError = Object.values(problem.errors ?? {})[0]?.[0]
    return firstFieldError ?? problem.detail ?? problem.title ?? `${response.status}`
  } catch {
    return `${response.status} ${response.statusText}`
  }
}

export type AuthMode = 'Development' | 'Entra'
export type UserKind = 'AcpStaff' | 'ClientUser'

export interface SignedIn {
  authenticated: true
  authMode: AuthMode
  userId: string
  displayName: string
  email: string
  kind: UserKind
  objectId: string
}

export interface SignedOut {
  authenticated: false
  authMode: AuthMode
  /** Where to send the browser to sign in. Null when the development picker is in use. */
  signInUrl: string | null
}

export type Session = SignedIn | SignedOut

/**
 * The only way the front end can know whether it is signed in: ask. A 401 here is an answer,
 * not a failure, so it does not go through the usual error path.
 */
export async function fetchSession(): Promise<Session> {
  const response = await fetch('/api/me', { credentials: 'same-origin', headers: json })

  if (response.status === 401) return (await response.json()) as SignedOut
  if (!response.ok) throw new ApiError(response.status, `${response.status} ${response.statusText}`)

  return (await response.json()) as SignedIn
}

export interface DevelopmentAccount {
  key: string
  email: string
  displayName: string
  guest: boolean
  source: 'configured' | 'invited'
}

export interface InvitationResult {
  userId: string
  email: string
  displayName: string
  kind: UserKind
  alreadyExisted: boolean
  redeemUrl: string
}

export interface PortalUser {
  id: string
  email: string
  displayName: string
  kind: UserKind
  createdAt: string
  lastSeenAt: string | null
  hasSignedIn: boolean
}

export interface HealthReport {
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  durationMs: number
  checks: Record<string, { status: string; error: string | null }>
}

export interface VersionInfo {
  name: string
  phase: number
  environment: string
  authMode: AuthMode
}

export const api = {
  health: () => get<HealthReport>('/api/health'),
  version: () => get<VersionInfo>('/api/version'),

  developmentAccounts: () => get<DevelopmentAccount[]>('/api/auth/dev-accounts'),
  developmentSignIn: (key: string) => post<void>('/api/auth/dev-login', { key }),

  users: () => get<PortalUser[]>('/api/acp/users'),
  inviteClientContact: (email: string, displayName: string) =>
    post<InvitationResult>('/api/acp/invitations', { email, displayName }),
}
