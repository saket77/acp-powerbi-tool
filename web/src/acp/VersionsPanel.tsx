import { useCallback, useEffect, useRef, useState } from 'react'
import { api, uploadToBlob, type ReportVersionView } from '../api'
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
  const [progress, setProgress] = useState<number | null>(null)
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

  /**
   * Three steps and a wait: ask the portal for somewhere to put the file, put it there, tell
   * the portal it arrived, then watch for the import and the template checks to finish.
   */
  async function upload(file: File) {
    setBusy(true)
    setError(null)
    setProgress(0)

    try {
      const staged = await api.startUpload(projectKey, file.name)

      if (file.size > staged.maxMegabytes * 1024 * 1024) {
        // Checked here as well as on the server, because finding out after sending 400 MB is a
        // bad way to find out.
        throw new Error(
          `That file is ${Math.round(file.size / 1024 / 1024)} MB and the limit is ${staged.maxMegabytes} MB.`,
        )
      }

      await uploadToBlob(staged.uploadUrl, file, setProgress)
      await api.completeUpload(projectKey, staged.uploadId)

      if (fileInput.current) fileInput.current.value = ''
      setProgress(null)
      await waitForImport(staged.version)
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Could not upload that file.')
    } finally {
      setBusy(false)
      setProgress(null)
    }
  }

  /**
   * The import runs on a queue, so the answer arrives a few seconds after the upload does.
   * Polling rather than pushing: this is one screen, watched by one person, for under a minute.
   */
  async function waitForImport(version: number) {
    const deadline = Date.now() + 3 * 60 * 1000

    while (Date.now() < deadline) {
      const all = await api.versions(projectKey)
      setVersions(all)
      setState({ kind: 'ready' })

      const row = all.find((v) => v.version === version)
      if (row && row.state !== 'Importing') return

      await new Promise((resolve) => setTimeout(resolve, 2000))
    }

    setError('The import is taking longer than expected. The page will catch up when it finishes.')
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
        The file goes straight to private storage and is imported in the background, then checked
        against the Databook template. What comes out is a draft only ACP can see — and a
        different lead has to release it before any client does.
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
        {busy && progress !== null && (
          <>
            <progress value={progress} max={1} aria-label="Upload progress" />
            <span className="hint">{Math.round(progress * 100)}% uploaded</span>
          </>
        )}
        {busy && progress === null && <span className="hint">Importing and checking…</span>}
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
                      {version.state === 'Importing' ? 'Checking…' : version.state}
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
