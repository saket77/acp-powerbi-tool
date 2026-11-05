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

async function send<T>(method: 'POST' | 'DELETE', path: string, body?: unknown): Promise<T> {
  const response = await fetch(path, {
    method,
    credentials: 'same-origin',
    headers: body === undefined ? json : { ...json, 'Content-Type': 'application/json' },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  })

  if (!response.ok) throw new ApiError(response.status, await describe(response))

  return response.status === 204 ? (undefined as T) : ((await response.json()) as T)
}

const post = <T,>(path: string, body: unknown) => send<T>('POST', path, body)
const del = (path: string) => send<void>('DELETE', path)

/**
 * Multipart upload. No Content-Type header on purpose — the browser has to set it itself so it
 * can include the multipart boundary, and naming it here would produce a request the server
 * cannot parse.
 */
async function postFile<T>(path: string, file: File): Promise<T> {
  const form = new FormData()
  form.append('file', file)

  const response = await fetch(path, {
    method: 'POST',
    credentials: 'same-origin',
    headers: json,
    body: form,
  })

  if (!response.ok) throw new ApiError(response.status, await describe(response))

  return (await response.json()) as T
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

export type ProjectRole = 'Lead' | 'Member' | 'Viewer' | 'Client'

export interface ClientSummary {
  slug: string
  name: string
  companyCount: number
  projectCount: number
  provisioned: boolean
}

export interface CompanyView {
  id: string
  code: string
  name: string
}

export interface ClientProject {
  key: string
  name: string
  status: string
  endDate: string
}

export interface ClientDetail {
  slug: string
  name: string
  /** False until the client has its own Power BI profile and workspace. */
  provisioned: boolean
  companies: CompanyView[]
  projects: ClientProject[]
}

export interface ProjectListItem {
  key: string
  name: string
  clientName: string
  clientSlug: string
  status: string
  endDate: string
  hasReleasedVersion: boolean
}

export interface ProjectDetail extends ProjectListItem {
  versionCount: number
}

export interface GrantView {
  id: string
  userId: string
  displayName: string
  email: string
  kind: UserKind
  role: ProjectRole
  endDate: string | null
  companies: string[]
}

export interface AcpProjectView {
  project: ProjectDetail
  grants: GrantView[]
}

export interface NewClientBody {
  name: string
  slug?: string
  companies: { code: string; name: string }[]
}

export interface NewGrantBody {
  userId: string
  role: ProjectRole
  endDate?: string
  companyIds?: string[]
}

export interface ReportVersionView {
  version: number
  state: 'Importing' | 'CheckFailed' | 'Draft' | 'Released' | 'Superseded'
  uploadedAt: string
  uploadedBy: string | null
  releasedAt: string | null
  releasedBy: string | null
  releaseNote: string | null
  checkFailureMessage: string | null
}

export interface EmbedScope {
  workspaceId: string
  datasetId: string
  roles: string[]
  username: string
  customData: string | null
  companies: string[]
}

export interface EmbedReady {
  status: 'ready'
  /** 'Fake' when Power BI is simulated — there is no capacity and nothing to render. */
  provider: 'Fake' | 'Live'
  embedUrl: string
  reportId: string
  token: string
  /** Power BI's own answer. Renewal is scheduled from this, never from the lifetime requested. */
  expiresAt: string
  version: number
  isDraft: boolean
  /** Only ever populated for ACP staff. */
  scope: EmbedScope | null
}

export interface EmbedUnavailable {
  status: 'unavailable'
  reason: string
}

export type EmbedResponse = EmbedReady | EmbedUnavailable

export const api = {
  health: () => get<HealthReport>('/api/health'),
  version: () => get<VersionInfo>('/api/version'),

  developmentAccounts: () => get<DevelopmentAccount[]>('/api/auth/dev-accounts'),
  developmentSignIn: (key: string) => post<void>('/api/auth/dev-login', { key }),

  users: () => get<PortalUser[]>('/api/acp/users'),
  inviteClientContact: (email: string, displayName: string) =>
    post<InvitationResult>('/api/acp/invitations', { email, displayName }),

  /** Scoped to the caller by row-level security, so it serves consultants and clients alike. */
  projects: () => get<ProjectListItem[]>('/api/projects'),
  projectSummary: (key: string) => get<ProjectDetail>(`/api/projects/${encodeURIComponent(key)}/summary`),

  clients: () => get<ClientSummary[]>('/api/acp/clients'),
  client: (slug: string) => get<ClientDetail>(`/api/acp/clients/${encodeURIComponent(slug)}`),
  createClient: (body: NewClientBody) => post<ClientDetail>('/api/acp/clients', body),

  acpProject: (key: string) => get<AcpProjectView>(`/api/acp/projects/${encodeURIComponent(key)}`),
  createProject: (body: { clientSlug: string; key?: string; name: string; endDate?: string }) =>
    post<ProjectDetail>('/api/acp/projects', body),

  provisionClient: (slug: string) =>
    post<ClientDetail>(`/api/acp/clients/${encodeURIComponent(slug)}/provision`, {}),

  versions: (projectKey: string) =>
    get<ReportVersionView[]>(`/api/acp/projects/${encodeURIComponent(projectKey)}/versions`),
  uploadVersion: (projectKey: string, file: File) =>
    postFile<ReportVersionView>(`/api/acp/projects/${encodeURIComponent(projectKey)}/versions`, file),
  releaseVersion: (projectKey: string, version: number, note: string) =>
    post<ReportVersionView>(
      `/api/acp/projects/${encodeURIComponent(projectKey)}/versions/${version}/release`,
      { note },
    ),

  embed: (projectKey: string, options?: { draft?: boolean; viewAs?: string }) => {
    const query = new URLSearchParams()
    if (options?.draft) query.set('draft', 'true')
    if (options?.viewAs) query.set('viewAs', options.viewAs)

    const suffix = query.size === 0 ? '' : `?${query}`
    return get<EmbedResponse>(`/api/projects/${encodeURIComponent(projectKey)}/embed${suffix}`)
  },

  setGrant: (projectKey: string, body: NewGrantBody) =>
    post<GrantView>(`/api/acp/projects/${encodeURIComponent(projectKey)}/grants`, body),
  revokeGrant: (projectKey: string, grantId: string) =>
    del(`/api/acp/projects/${encodeURIComponent(projectKey)}/grants/${grantId}`),
}
