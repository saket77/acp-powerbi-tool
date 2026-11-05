import { useCallback, useEffect, useRef, useState } from 'react'
import { PowerBIEmbed } from 'powerbi-client-react'
import { models } from 'powerbi-client'
import { api, ApiError, type EmbedReady, type EmbedResponse } from '../api'
import { StateBoundary, type LoadState } from './LoadState'

/** Renew this long before the token expires, so a slow network cannot land us past it. */
const RenewalMarginMs = 2 * 60 * 1000
const MinimumRenewalDelayMs = 30 * 1000

/**
 * Renders one Databook, and keeps it rendering.
 *
 * The token is short-lived by design, so the interesting part of this component is not the
 * embed — that is four lines of someone else's SDK — but the renewal. It schedules from the
 * expiry the server reported rather than from the lifetime the server asked for, because an
 * embed token cannot outlive the Entra token it was minted with and therefore sometimes comes
 * back shorter than requested.
 */
export function ReportView({
  projectKey,
  draft,
  viewAs,
}: {
  projectKey: string
  draft?: boolean
  viewAs?: string
}) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [embed, setEmbed] = useState<EmbedReady | null>(null)
  const [unavailable, setUnavailable] = useState<string | null>(null)
  const timer = useRef<number | undefined>(undefined)

  const load = useCallback(async () => {
    try {
      const options: { draft?: boolean; viewAs?: string } = {}
      if (draft) options.draft = true
      if (viewAs) options.viewAs = viewAs

      const response: EmbedResponse = await api.embed(projectKey, options)

      if (response.status === 'unavailable') {
        setEmbed(null)
        setUnavailable(response.reason)
        setState({ kind: 'empty' })
        return
      }

      setUnavailable(null)
      setEmbed(response)
      setState({ kind: 'ready' })

      const msUntilExpiry = new Date(response.expiresAt).getTime() - Date.now()
      const delay = Math.max(msUntilExpiry - RenewalMarginMs, MinimumRenewalDelayMs)

      window.clearTimeout(timer.current)
      timer.current = window.setTimeout(() => void load(), delay)
    } catch (e: unknown) {
      setEmbed(null)
      setState(
        e instanceof ApiError && e.status === 404
          ? { kind: 'noAccess' }
          : { kind: 'error', message: e instanceof Error ? e.message : 'Could not open the Databook.' },
      )
    }
  }, [projectKey, draft, viewAs])

  useEffect(() => {
    void load()
    return () => window.clearTimeout(timer.current)
  }, [load])

  return (
    <StateBoundary state={state} emptyMessage={unavailable ?? 'Nothing to show yet.'}>
      {embed &&
        (embed.provider === 'Fake' ? (
          <SimulatedReport embed={embed} />
        ) : (
          <PowerBIEmbed
            cssClassName="report-frame"
            embedConfig={{
              type: 'report',
              id: embed.reportId,
              embedUrl: embed.embedUrl,
              accessToken: embed.token,
              tokenType: models.TokenType.Embed,
              settings: {
                // Clients get a report, not a report-building tool. Everything that would let
                // somebody reach past their own filter is off.
                panes: {
                  filters: { visible: false, expanded: false },
                  pageNavigation: { visible: true },
                },
                background: models.BackgroundType.Transparent,
              },
            }}
          />
        ))}
    </StateBoundary>
  )
}

/**
 * What stands in for the report when Power BI is simulated.
 *
 * Deliberately not a grey box saying "preview unavailable". It shows exactly what the token
 * says, because that is the half of the system which *is* being exercised — and because a
 * screen that looked like a working report would be the most expensive kind of wrong.
 */
function SimulatedReport({ embed }: { embed: EmbedReady }) {
  return (
    <div className="simulated">
      <p className="row" style={{ marginTop: 0 }}>
        <strong>Power BI is simulated.</strong>
        <span className="badge">version {embed.version}</span>
        {embed.isDraft && <span className="badge">draft</span>}
      </p>
      <p className="hint">
        No capacity exists, so there is no report to render. The portal still did everything on
        its own side of the line — resolved the grant, read the company codes from the database
        and asked for a token scoped to one report and one model. Whether Power BI honours that
        scope is what Gate 1 measures.
      </p>

      {embed.scope ? (
        <dl className="scope">
          <dt>Report</dt>
          <dd className="mono">{embed.reportId}</dd>
          <dt>Semantic model</dt>
          <dd className="mono">{embed.scope.datasetId}</dd>
          <dt>Workspace</dt>
          <dd className="mono">{embed.scope.workspaceId}</dd>
          <dt>Role</dt>
          <dd>{embed.scope.roles.join(', ')}</dd>
          <dt>Identity</dt>
          <dd className="mono">{embed.scope.customData ?? embed.scope.username}</dd>
          <dt>Companies</dt>
          <dd>{embed.scope.companies.length === 0 ? 'all' : embed.scope.companies.join(', ')}</dd>
          <dt>Token expires</dt>
          <dd>{new Date(embed.expiresAt).toLocaleTimeString()}</dd>
        </dl>
      ) : (
        <p className="hint">Your Databook will appear here once Power BI is connected.</p>
      )}
    </div>
  )
}
