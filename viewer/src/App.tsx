import { useEffect, useState } from 'react'
import { CallFlowView } from './components/CallFlowView'
import { EntitiesView } from './components/EntitiesView'
import { OverviewView } from './components/OverviewView'
import { ScoreView } from './components/ScoreView'
import { Search } from './components/Search'
import { SidePanel } from './components/SidePanel'
import { parseGraph } from './lib/graph'
import type { ArchGraph } from './lib/types'

type Tab = 'overview' | 'entities' | 'callflow' | 'score'

const TABS: { id: Tab; label: string }[] = [
  { id: 'overview', label: 'Overview' },
  { id: 'entities', label: 'Data Entities' },
  { id: 'callflow', label: 'Call Flow' },
  { id: 'score', label: 'Score' },
]

interface Example {
  name: string
  file: string
}

const examplesUrl = (file: string) => `${import.meta.env.BASE_URL}examples/${file}`

export default function App() {
  const [graph, setGraph] = useState<ArchGraph | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [examples, setExamples] = useState<Example[]>([])
  const [tab, setTab] = useState<Tab>('overview')
  const [selectedId, setSelectedId] = useState<string | undefined>()
  // Bumped on every load so each view starts fresh (no expanded boxes left over from the last graph).
  const [loadCount, setLoadCount] = useState(0)

  const show = (raw: unknown) => {
    try {
      setGraph(parseGraph(raw))
      setLoadCount((n) => n + 1)
      setSelectedId(undefined)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const loadExample = async (file: string) => {
    try {
      const res = await fetch(examplesUrl(file))
      if (!res.ok) throw new Error(`Could not load ${file} (HTTP ${res.status}).`)
      show(await res.json())
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const loadFile = async (file: File) => {
    try {
      show(JSON.parse(await file.text()))
    } catch (e) {
      setError(`${file.name}: ${e instanceof Error ? e.message : String(e)}`)
    }
  }

  // Open the first bundled example so a first-time visitor sees a diagram right away.
  useEffect(() => {
    fetch(examplesUrl('index.json'))
      .then((r) => (r.ok ? r.json() : []))
      .then((list: Example[]) => {
        setExamples(list)
        if (list[0]) void loadExample(list[0].file)
      })
      .catch(() => setExamples([]))
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
        <strong className="brand">ArchLens</strong>
        {examples.length > 0 && (
          <select defaultValue="" onChange={(e) => e.target.value && void loadExample(e.target.value)}>
            <option value="" disabled>
              Examples…
            </option>
            {examples.map((x) => (
              <option key={x.file} value={x.file}>
                {x.name}
              </option>
            ))}
          </select>
        )}
        <label className="file-button">
          Open graph JSON
          <input
            type="file"
            accept=".json,application/json"
            onChange={(e) => {
              const file = e.target.files?.[0]
              if (file) void loadFile(file)
              e.target.value = ''
            }}
          />
        </label>
        <button onClick={exportJson} disabled={!graph}>
          Export JSON
        </button>
        {graph && <Search graph={graph} onSelect={setSelectedId} />}
        {graph && (
          <span className="muted repo">
            {graph.repo}
            {graph.commit && ` @ ${graph.commit.slice(0, 7)}`} · {graph.elements.length} boxes,{' '}
            {graph.relationships.length} arrows
          </span>
        )}
      </header>

      {error && (
        <div className="error" role="alert">
          {error} <button onClick={() => setError(null)}>dismiss</button>
        </div>
      )}

      {!graph ? (
        <div className="empty">Drop an ArchLens graph JSON here, or open one above.</div>
      ) : (
        <>
          <nav className="tabs">
            {TABS.map((t) => (
              <button key={t.id} className={t.id === tab ? 'active' : ''} onClick={() => setTab(t.id)}>
                {t.label}
              </button>
            ))}
          </nav>
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
        </>
      )}
    </div>
  )
}
