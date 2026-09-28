import { useMemo, useState } from 'react'
import { displayName, indexById, searchElements } from '../lib/graph'
import type { ArchGraph } from '../lib/types'
import { TYPE_LABEL } from './nodes'

interface Props {
  graph: ArchGraph
  onSelect: (id: string) => void
}

// Type a class or method name; Enter opens the best match, or click any result.
export function Search({ graph, onSelect }: Props) {
  const [query, setQuery] = useState('')
  const byId = useMemo(() => indexById(graph), [graph])
  const results = useMemo(() => searchElements(graph, query, 12), [graph, query])

  const pick = (id: string) => {
    onSelect(id)
    setQuery('')
  }

  return (
    <div className="search">
      <input
        type="search"
        placeholder="Search class or method"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter' && results[0]) pick(results[0].id)
          if (e.key === 'Escape') setQuery('')
        }}
      />
      {results.length > 0 && (
        <ul className="search-results">
          {results.map((el) => (
            <li key={el.id}>
              <button onClick={() => pick(el.id)}>
                {displayName(el, byId)} <span className="muted">{TYPE_LABEL[el.type]}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
