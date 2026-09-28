import { Background, BackgroundVariant, Controls, MiniMap, ReactFlow, type Edge } from '@xyflow/react'
import { useEffect, useMemo, useState } from 'react'
import { toFlowNodes, toLayoutNodes, type ElementNode, type FlowItem } from '../lib/flow'
import { layoutGraph } from '../lib/layout'
import { Legend } from './Legend'
import { nodeTypes } from './nodes'

// Minimap dots use the same hue as each box type, so you can spot data vs code at a glance.
const MINIMAP_COLORS: Record<string, string> = {
  ApplicationComponent: '#3b82f6',
  ApplicationInterface: '#6366f1',
  ApplicationService: '#60a5fa',
  ApplicationFunction: '#93c5fd',
  DataObject: '#f59e0b',
  ExternalSystem: '#94a3b8',
  TechnologyService: '#10b981',
  Node: '#10b981',
  BusinessActor: '#eab308',
}

interface Props {
  items: FlowItem[]
  edges: Edge[]
  selectedId?: string
  onNodeClick: (id: string) => void
}

// Lays out the boxes with ELK, then hands the positioned boxes to React Flow.
// Callers must memoize items and edges, or the layout would rerun on every render.
export function Canvas({ items, edges, selectedId, onNodeClick }: Props) {
  const [laidOut, setLaidOut] = useState<{ nodes: ElementNode[]; version: number } | null>(null)

  useEffect(() => {
    let cancelled = false
    const layoutEdges = edges.map((e) => ({ id: e.id, source: e.source, target: e.target }))
    layoutGraph(toLayoutNodes(items), layoutEdges, 'DOWN').then((boxes) => {
      // A newer layout may have started while this one ran; only the latest may draw.
      if (!cancelled) setLaidOut((prev) => ({ nodes: toFlowNodes(items, boxes), version: (prev?.version ?? 0) + 1 }))
    })
    return () => {
      cancelled = true
    }
  }, [items, edges])

  const nodes = useMemo(
    () => laidOut?.nodes.map((n) => ({ ...n, selected: n.id === selectedId })) ?? [],
    [laidOut, selectedId],
  )

  if (items.length === 0) return <div className="empty">Nothing to show in this view.</div>
  if (!laidOut) return <div className="empty"><span className="spinner" /> Laying out…</div>

  return (
    <div className="canvas">
      {/* The key remounts React Flow after each new layout so fitView frames the new boxes. */}
      <ReactFlow
        key={laidOut.version}
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        onNodeClick={(_, node) => onNodeClick(node.id)}
        nodesDraggable={false}
        nodesConnectable={false}
        fitView
        fitViewOptions={{ padding: 0.15 }}
        minZoom={0.05}
      >
        <Background variant={BackgroundVariant.Dots} gap={18} size={1.2} color="var(--grid)" />
        <Controls showInteractive={false} position="bottom-right" />
        <MiniMap
          position="top-right"
          pannable
          zoomable
          nodeColor={(n) => MINIMAP_COLORS[(n.data as ElementNode['data']).element.type] ?? '#94a3b8'}
          nodeStrokeWidth={0}
          maskColor="var(--minimap-mask)"
        />
      </ReactFlow>
      <Legend />
    </div>
  )
}
