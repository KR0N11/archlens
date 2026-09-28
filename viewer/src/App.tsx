import { useCallback, useEffect, useRef, useState } from 'react'
import { CallFlowView } from './components/CallFlowView'
import { EntitiesView } from './components/EntitiesView'
import { Landing, LogoMark, type Example } from './components/Landing'
import { OverviewView } from './components/OverviewView'
import { Progress } from './components/Progress'
import { ScoreView } from './components/ScoreView'
import { Search } from './components/Search'
import { SidePanel } from './components/SidePanel'
import { useAnalysis } from './components/useAnalysis'
import { apiIsUp, normalizeRepoUrl } from './lib/api'
import { githubRepoUrl, parseGraph } from './lib/graph'
import type { ArchGraph } from './lib/types'

type Tab = 'overview' | 'entities' | 'callflow' | 'score'

const TABS: { id: Tab; label: string }[] = [
  { id: 'overview', label: 'Overview' },
  { id: 'entities', label: 'Data Entities' },
  { id: 'callflow', label: 'Call Flow' },
  { id: 'score', label: 'Score' },
]

const examplesUrl = (file: string) => `${import.meta.env.BASE_URL}examples/${file}`

// ?repo=owner/name keeps the current repo in the address bar, so a refresh or a shared
// link runs the same analysis again (the server's cache makes the rerun fast).
function repoFromUrl(): string | null {
  const repo = new URLSearchParams(window.location.search).get('repo')
  return repo ? normalizeRepoUrl(`github.com/${repo}`) : null
}

function setRepoInUrl(repo: string | null) {
  const url = new URL(window.location.href)
  if (repo) url.searchParams.set('repo', repo.replace(/^github\.com\//, ''))
  else url.searchParams.delete('repo')
  window.history.replaceState(null, '', url)
}

export default function App() {
  const [graph, setGraph] = useState<ArchGraph | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [examples, setExamples] = useState<Example[]>([])
  const [apiUp, setApiUp] = useState<boolean | null>(null)
  const [tab, setTab] = useState<Tab>('overview')
  const [selectedId, setSelectedId] = useState<string | undefined>()
  // Bumped on every load so each view starts fresh (no expanded boxes left over from the last graph).
  const [loadCount, setLoadCount] = useState(0)

  const show = useCallback((raw: unknown) => {
    try {
      setGraph(parseGraph(raw))
      setLoadCount((n) => n + 1)
      setSelectedId(undefined)
      setTab('overview')
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  const { state: run, start, cancel } = useAnalysis(show)

  const analyze = (repo: string) => {
    setError(null)
    setRepoInUrl(repo)
    void start(repo)
  }

  const loadExample = async (file: string) => {
    try {
      const res = await fetch(examplesUrl(file))
      if (!res.ok) throw new Error(`Could not load ${file} (HTTP ${res.status}).`)
      setRepoInUrl(null)
      show(await res.json())
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const loadFile = async (file: File) => {
    try {
      const raw = JSON.parse(await file.text())
      setRepoInUrl(null)
      show(raw)
    } catch (e) {
      setError(`${file.name}: ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  const newAnalysis = () => {
    cancel()
    setGraph(null)
    setSelectedId(undefined)
    setError(null)
    setRepoInUrl(null)
  }

  // React's dev mode runs start-up effects twice; this makes sure ?repo= only starts one analysis.
  const autoStarted = useRef(false)

  // On start: list the examples (nothing is opened), check the API, and rerun ?repo= if present.
  useEffect(() => {
    fetch(examplesUrl('index.json'))
      .then((r) => (r.ok ? r.json() : []))
      .then((list: Example[]) => setExamples(list))
      .catch(() => setExamples([]))
    void apiIsUp().then(setApiUp)
    const repo = repoFromUrl()
    if (repo && !autoStarted.current) {
      autoStarted.current = true
      void start(repo)
    }
    // Runs once on start.
  }, [])

  const exportJson = () => {
    if (!graph) return
    const blob = new Blob([JSON.stringify(graph, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `${graph.repo.split('/').pop() || 'graph'}${graph.commit ? `-${graph.commit.slice(0, 7)}` : ''}.json`
    a.click()
    URL.revokeObjectURL(url)
  }

  const repoLink = graph ? githubRepoUrl(graph.repo, graph.commit) : undefined

  let body
  if (run.phase !== 'idle') {
    body = <Progress state={run} onCancel={newAnalysis} onRetry={() => analyze(run.repo)} />
  } else if (!graph) {
    body = (
      <Landing
        examples={examples}
        apiUp={apiUp}
        initialRepo={repoFromUrl() ?? undefined}
        error={error ?? undefined}
        onAnalyze={analyze}
        onExample={(file) => void loadExample(file)}
        onFile={(file) => void loadFile(file)}
      />
    )
  } else {
    body = (
      <main className="main">
        <section className="view" key={loadCount}>
          {tab === 'overview' && <OverviewView graph={graph} selectedId={selectedId} onSelect={setSelectedId} />}
          {tab === 'entities' && <EntitiesView graph={graph} selectedId={selectedId} onSelect={setSelectedId} />}
          {tab === 'callflow' && <CallFlowView graph={graph} selectedId={selectedId} onSelect={setSelectedId} />}
          {tab === 'score' && <ScoreView graph={graph} onSelect={setSelectedId} />}
        </section>
        {selectedId && (
          <SidePanel graph={graph} id={selectedId} onSelect={setSelectedId} onClose={() => setSelectedId(undefined)} />
        )}
      </main>
    )
  }

  return (
    <div
      className="app"
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        e.preventDefault()
        const file = e.dataTransfer.files[0]
        if (file) void loadFile(file)
      }}
    >
      <header className="topbar">
        <button className="brand" onClick={newAnalysis} title="Back to the start">
          <LogoMark />
          <span>ArchLens</span>
        </button>
        {graph && run.phase === 'idle' && (
          <>
            <a className="repo-chip" href={repoLink} target="_blank" rel="noreferrer" aria-disabled={!repoLink}>
              <span>{graph.repo}</span>
              {graph.commit && <code>{graph.commit.slice(0, 7)}</code>}
            </a>
            <span className="counts">
              {graph.elements.length.toLocaleString()} boxes · {graph.relationships.length.toLocaleString()} arrows
            </span>
            <nav className="segmented">
              {TABS.map((t) => (
                <button key={t.id} className={t.id === tab ? 'active' : ''} onClick={() => setTab(t.id)}>
                  {t.label}
                </button>
              ))}
            </nav>
            <div className="topbar-right">
              <Search graph={graph} onSelect={setSelectedId} />
              <button onClick={exportJson}>Export JSON</button>
              <button className="primary" onClick={newAnalysis}>
                New analysis
              </button>
            </div>
          </>
        )}
      </header>

      {error && graph && (
        <div className="error" role="alert">
          {error} <button onClick={() => setError(null)}>Dismiss</button>
        </div>
      )}

      {body}
    </div>
  )
}
