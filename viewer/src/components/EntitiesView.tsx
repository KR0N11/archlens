import { useMemo, useState } from 'react'
import { toFlowEdges, type FlowItem } from '../lib/flow'
import { crudMatrix, dataEntitiesSubgraph, LOW_CONFIDENCE } from '../lib/graph'
import type { ArchGraph } from '../lib/types'
import { Canvas } from './Canvas'

interface Props {
  graph: ArchGraph
  selectedId?: string
  onSelect: (id: string) => void
}

// What data exists (boxes with fields) and who touches it (the CRUD matrix underneath).
export function EntitiesView({ graph, selectedId, onSelect }: Props) {
  const [showUnsure, setShowUnsure] = useState(false)
  const unsureCount = useMemo(
    () => graph.elements.filter((e) => e.type === 'DataObject' && (e.confidence ?? 1) < LOW_CONFIDENCE).length,
    [graph],
  )
  const { items, edges } = useMemo(() => {
    const sub = dataEntitiesSubgraph(graph, showUnsure)
    const items: FlowItem[] = sub.elements.map((el) => ({
      id: el.id,
      isGroup: false,
      data: { element: el, label: el.name, showFields: true },
    }))
    return { items, edges: toFlowEdges(sub.relationships) }
  }, [graph, showUnsure])

  const matrix = useMemo(() => crudMatrix(graph), [graph])

  return (
    <div className="split">
      <Canvas items={items} edges={edges} selectedId={selectedId} onNodeClick={onSelect} />
      <div className="crud">
        <label className="toggle">
          <input type="checkbox" checked={showUnsure} onChange={(e) => setShowUnsure(e.target.checked)} />
          Show {unsureCount} unsure entities (score 2 to 4, not checked by the LLM)
        </label>
        <h3>Who reads and writes each entity</h3>
        {matrix.components.length === 0 ? (
          <p className="muted">No read or write arrows were found.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>Entity</th>
                {matrix.components.map((c) => (
                  <th key={c.id}>{c.name}</th>
                ))}
              </tr>
            </thead>
            <tbody>
              {matrix.entities.map((e) => (
                <tr key={e.id} className={e.id === selectedId ? 'row-selected' : ''}>
                  <td>
                    <button className="link" onClick={() => onSelect(e.id)}>
                      {e.name}
                    </button>
                  </td>
                  {matrix.components.map((c) => (
                    <td key={c.id} className="crud-cell">
                      {matrix.cells[e.id]?.[c.id] ?? ''}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}
