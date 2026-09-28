import { useMemo, useState } from 'react'
import { toFlowEdges, type FlowItem } from '../lib/flow'
import { biggestFlow, callFlowViews, displayName, indexById, viewSubgraph } from '../lib/graph'
import type { ArchGraph } from '../lib/types'
import { Canvas } from './Canvas'

interface Props {
  graph: ArchGraph
  selectedId?: string
  onSelect: (id: string) => void
}

// Pick one entry point; the analyzer already traced its path, this view only draws it.
export function CallFlowView({ graph, selectedId, onSelect }: Props) {
  const options = useMemo(() => callFlowViews(graph), [graph])
  const [chosen, setChosen] = useState<string | undefined>(undefined)
  // Fall back to the longest flow when nothing is chosen yet, or the old choice
  // belongs to a graph that was just replaced.
  const key = options.some((o) => o.key === chosen) ? chosen! : biggestFlow(options)?.key

  const { items, edges } = useMemo(() => {
    if (!key) return { items: [], edges: [] }
    const byId = indexById(graph)
    const sub = viewSubgraph(graph, graph.views[key])
    const items: FlowItem[] = sub.elements.map((el) => ({
      id: el.id,
      isGroup: false,
      data: { element: el, label: displayName(el, byId), showFields: false },
    }))
    return { items, edges: toFlowEdges(sub.relationships) }
  }, [graph, key])

  if (options.length === 0) return <div className="empty">This graph has no entry points to trace.</div>

  return (
    <div className="split-top">
      <div className="toolbar">
        <label>
          Entry point{' '}
          <select value={key} onChange={(e) => setChosen(e.target.value)}>
            {options.map((o) => (
              <option key={o.key} value={o.key}>
                {o.label}
              </option>
            ))}
          </select>
        </label>
        <span className="muted">
          {items.length} boxes, {edges.length} arrows
        </span>
      </div>
      <Canvas items={items} edges={edges} selectedId={selectedId} onNodeClick={onSelect} />
    </div>
  )
}
