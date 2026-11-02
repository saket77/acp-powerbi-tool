/**
 * The one place that talks to the backend.
 *
 * Every call is same-origin through the Vite proxy in development and through the same host in
 * production, and every call sends credentials, because the session is an HttpOnly cookie the
 * JavaScript here can neither read nor set. Phase 2 adds sign-in on top of exactly this.
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

async function get<T>(path: string): Promise<T> {
  const response = await fetch(path, {
    credentials: 'same-origin',
    headers: { Accept: 'application/json' },
  })

  if (!response.ok) {
    // 404 covers both "no such thing" and "not yours". The UI must not try to tell them apart,
    // because the backend deliberately does not.
    throw new ApiError(response.status, `${response.status} ${response.statusText}`)
  }

  return (await response.json()) as T
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
}

export const api = {
  health: () => get<HealthReport>('/api/health'),
  version: () => get<VersionInfo>('/api/version'),
}
