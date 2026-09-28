import { useMemo, useState } from 'react'
import { toCountEdges, type FlowItem } from '../lib/flow'
import { displayName, indexById, overviewModel } from '../lib/graph'
import type { ArchGraph } from '../lib/types'
import { Canvas } from './Canvas'

interface Props {
  graph: ArchGraph
  selectedId?: string
  onSelect: (id: string) => void
}

// Components only, with arrows merged between them. Click a component to open it up.
export function OverviewView({ graph, selectedId, onSelect }: Props) {
  const [expanded, setExpanded] = useState<Set<string>>(new Set())

  const { items, edges } = useMemo(() => {
    const byId = indexById(graph)
    const model = overviewModel(graph, expanded)
    const withChildren = new Set(model.nodes.filter((x) => x.parent).map((x) => x.parent!))
    const items: FlowItem[] = model.nodes.map((x) => ({
      id: x.element.id,
      parent: x.parent,
      isGroup: withChildren.has(x.element.id),
      data: {
        element: x.element,
        label: displayName(x.element, byId),
        showFields: false,
        expandable: x.element.type === 'ApplicationComponent' && !x.expanded,
        expanded: x.expanded,
      },
    }))
    return { items, edges: toCountEdges(model.edges) }
  }, [graph, expanded])

  const handleClick = (id: string) => {
    onSelect(id)
    const clicked = items.find((i) => i.id === id)
    if (clicked?.data.element.type !== 'ApplicationComponent') return
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  return <Canvas items={items} edges={edges} selectedId={selectedId} onNodeClick={handleClick} />
}
