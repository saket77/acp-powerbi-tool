import { useCallback, useEffect, useRef, useState } from 'react'
import { api, type ReportVersionView } from '../api'
import { StateBoundary, type LoadState } from '../shared/LoadState'

/**
 * Uploading and releasing Databooks.
 *
 * Release is somebody else's job by design: the person who uploaded a version cannot be the one
 * who decides a client should see it. The button is shown anyway rather than hidden, because a
 * refusal that explains itself teaches the rule and a missing button does not.
 */
export function VersionsPanel({ projectKey, onReleased }: { projectKey: string; onReleased: () => void }) {
  const [state, setState] = useState<LoadState>({ kind: 'loading' })
  const [versions, setVersions] = useState<ReportVersionView[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)

  const load = useCallback(async () => {
    try {
      const all = await api.versions(projectKey)
      setVersions(all)
      setState(all.length === 0 ? { kind: 'empty' } : { kind: 'ready' })
    } catch (e: unknown) {
      setState({ kind: 'error', message: e instanceof Error ? e.message : 'Could not load versions.' })
    }
  }, [projectKey])

  useEffect(() => {
    void load()
  }, [load])

  async function upload(file: File) {
    setBusy(true)
    setError(null)
    try {
      await api.uploadVersion(projectKey, file)
      if (fileInput.current) fileInput.current.value = ''
      await load()
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Could not upload that file.')
    } finally {
      setBusy(false)
    }
  }

  async function release(version: number) {
    setBusy(true)
    setError(null)
    try {
      await api.releaseVersion(projectKey, version, '')
      await load()
      onReleased()
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Could not release that version.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="card">
      <h2>Databook versions</h2>
      <p className="hint">
        Uploading creates a draft only you and other ACP people can see. A different lead has to
        release it before any client does.
      </p>

      <div className="row">
        <input
          ref={fileInput}
          type="file"
          accept=".pbix"
          disabled={busy}
          aria-label="Databook .pbix file"
          onChange={(e) => {
            const file = e.target.files?.[0]
            if (file) void upload(file)
          }}
        />
        {busy && <span className="hint">Working…</span>}
      </div>
      {error && <p className="error">{error}</p>}

      <div style={{ marginTop: '1rem' }}>
        <StateBoundary state={state} emptyMessage="Nothing uploaded yet.">
          <table>
            <thead>
              <tr>
                <th>Version</th>
                <th>State</th>
                <th>Uploaded</th>
                <th>Released</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {versions.map((version) => (
                <tr key={version.version}>
                  <td>v{version.version}</td>
                  <td>
                    <span className={version.state === 'Released' ? 'badge staff' : 'badge'}>
                      {version.state}
                    </span>
                    {version.checkFailureMessage && (
                      <p className="error" style={{ margin: '0.25rem 0 0' }}>
                        {version.checkFailureMessage}
                      </p>
                    )}
                  </td>
                  <td>
                    {version.uploadedBy ?? 'somebody'}
                    <br />
                    <span className="hint">{new Date(version.uploadedAt).toLocaleString()}</span>
                  </td>
                  <td>
                    {version.releasedBy ? (
                      <>
                        {version.releasedBy}
                        <br />
                        <span className="hint">
                          {new Date(version.releasedAt!).toLocaleString()}
                        </span>
                      </>
                    ) : (
                      <span className="hint">—</span>
                    )}
                  </td>
                  <td>
                    {version.state === 'Draft' && (
                      <button disabled={busy} onClick={() => void release(version.version)}>
                        Release
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </StateBoundary>
      </div>
    </section>
  )
}
