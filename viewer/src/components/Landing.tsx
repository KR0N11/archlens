import { useState } from 'react'
import { normalizeRepoUrl } from '../lib/api'

export interface Example {
  name: string
  file: string
}

interface Props {
  examples: Example[]
  apiUp: boolean | null
  initialRepo?: string
  error?: string
  onAnalyze: (repo: string) => void
  onExample: (file: string) => void
  onFile: (file: File) => void
}

// The first screen: paste a repo link. Examples and saved JSON files are the fallback.
export function Landing({ examples, apiUp, initialRepo, error, onAnalyze, onExample, onFile }: Props) {
  const [input, setInput] = useState(initialRepo ?? '')
  const [invalid, setInvalid] = useState(false)

  const submit = () => {
    const repo = normalizeRepoUrl(input)
    setInvalid(repo === null)
    if (repo) onAnalyze(repo)
  }

  return (
    <div className="landing">
      <div className="landing-inner">
        <div className="landing-mark" aria-hidden>
          <LogoMark size={44} />
        </div>
        <h1>ArchLens</h1>
        <p className="tagline">Paste a C# repo. Get its architecture, with the line of code behind every arrow.</p>

        <form
          className="repo-form"
          onSubmit={(e) => {
            e.preventDefault()
            submit()
          }}
        >
          <input
            className={invalid ? 'invalid' : ''}
            autoFocus
            spellCheck={false}
            placeholder="Paste a GitHub repo URL (C# / .NET)"
            value={input}
            onChange={(e) => {
              setInput(e.target.value)
              setInvalid(false)
            }}
          />
          <button type="submit" className="primary" disabled={!input.trim()}>
            Analyze
          </button>
        </form>
        {invalid && <p className="form-error">That doesn't look like github.com/owner/repo.</p>}
        {!invalid && error && <p className="form-error">{error}</p>}
        {apiUp === false && (
          <p className="api-down">
            The analyzer API isn't running. Start it with:{' '}
            <code>dotnet run --project src/ArchLens.Api</code>
          </p>
        )}

        <div className="landing-secondary">
          {examples.length > 0 && (
            <div className="examples">
              <span className="muted">or try an example:</span>
              {examples.map((x) => (
                <button key={x.file} className="chip" onClick={() => onExample(x.file)}>
                  {x.name}
                </button>
              ))}
            </div>
          )}
          <label className="file-link">
            Open a saved graph JSON
            <input
              type="file"
              accept=".json,application/json"
              onChange={(e) => {
                const file = e.target.files?.[0]
                if (file) onFile(file)
                e.target.value = ''
              }}
            />
          </label>
          <span className="muted small">You can also drop a graph JSON anywhere on the page.</span>
        </div>
      </div>
    </div>
  )
}

// Three stacked layers with a lens ring: the ArchiMate layers seen through a magnifier.
export function LogoMark({ size = 22 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 32 32" fill="none">
      <rect x="3" y="5" width="20" height="5" rx="1.5" fill="var(--c-business)" />
      <rect x="3" y="13" width="20" height="5" rx="1.5" fill="var(--c-app)" />
      <rect x="3" y="21" width="20" height="5" rx="1.5" fill="var(--c-tech)" />
      <circle cx="22" cy="18" r="6.5" stroke="var(--accent)" strokeWidth="2.5" fill="var(--surface)" fillOpacity=".55" />
      <line x1="26.6" y1="22.6" x2="30" y2="26" stroke="var(--accent)" strokeWidth="2.5" strokeLinecap="round" />
    </svg>
  )
}
